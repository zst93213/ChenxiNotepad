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
        // 关键修复1：去除所有路径末尾的反斜杠，避免与 bat 引号冲突导致语法错误
        appDir = appDir.TrimEnd('\\', '/');
        newFilesDir = newFilesDir.TrimEnd('\\', '/');
        appExePath = appExePath.TrimEnd('\\', '/');
        tempZipPath = tempZipPath.TrimEnd('\\', '/');

        // 关键修复2：自动下钻。如果 zip 解压后多一层目录（BlindNotepad_v2.x.x/），
        // 导致 newFilesDir 下没有 SuixinJi.exe，自动进入该子目录
        var exeName = Path.GetFileName(appExePath) ?? "SuixinJi.exe";
        if (!File.Exists(Path.Combine(newFilesDir, exeName)))
        {
            try
            {
                var subDirs = Directory.GetDirectories(newFilesDir);
                foreach (var sd in subDirs)
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

        // 日志写到 data/update_logs 目录，用户容易找到
        var updateLogDir = Path.Combine(appDir, "data", "update_logs");
        try { Directory.CreateDirectory(updateLogDir); } catch { }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var logPath = Path.Combine(updateLogDir, $"update_{timestamp}.log");
        // bat 文件固定放在 appDir 下，避免临时目录被清理
        var batPath = Path.Combine(appDir, "Update.bat");

        // 关键修复3：C# 端先立即写一条日志，保证即便 bat 一开头就崩也有轨迹
        try
        {
            File.WriteAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ===== C# LaunchUpdater Entry =====\n" +
                $"  batPath = {batPath}\n" +
                $"  appDir  = {appDir}\n" +
                $"  newFilesDir = {newFilesDir}\n" +
                $"  appExePath  = {appExePath}\n" +
                $"  tempZipPath = {tempZipPath}\n" +
                $"  currentPID  = {pid}\n" +
                $"  exeName     = {exeName}\n" +
                $"  newDirHasExe = {File.Exists(Path.Combine(newFilesDir, exeName))}\n\n",
                new System.Text.UTF8Encoding(false));
        }
        catch { }

        // 使用占位符替换（不使用 C# 字符串插值 $），
        // 彻底避免逐字字符串中双引号""和批处理语法冲突导致编译失败
        var batContent = @"@echo off
REM ===== 关键修复：路径中可能有 ! 或 ^ 等字符，在启用 delayed expansion 前先赋值，
REM ===== 然后在需要访问路径变量时显式 setlocal DisableDelayedExpansion 防止被吞
setlocal DisableDelayedExpansion

chcp 65001 >nul

set ""LOGFILE=__LOGFILE__""
set ""APPDIR=__APPDIR__""
set ""NEWDIR=__NEWDIR__""
set ""EXEPATH=__EXEPATH__""
set ""ZIPPATH=__ZIPPATH__""
set ""PID=__PID__""
set ""BATFILE=__BATFILE__""
set ""EXENAME=__EXENAME__""

REM ===== 立即写一条bat启动成功标记 =====
call :RAWLOG [%date% %time%] ===== BAT LAUNCHED OK (stage 0: before delayed expansion) =====
call :RAWLOG [%date% %time%] LOGFILE=%LOGFILE%
call :RAWLOG [%date% %time%] APPDIR=%APPDIR%
call :RAWLOG [%date% %time%] NEWDIR=%NEWDIR%
call :RAWLOG [%date% %time%] EXEPATH=%EXEPATH%

REM ===== 进入正式逻辑后再启用 delayed expansion（仅限数字/标志变量使用） =====
setlocal EnableDelayedExpansion

call :LOG ===== SuixinJi Update Start (stage 1: delayed expansion enabled) =====

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
REM 操作路径时禁用 delayed expansion，防止路径中 ! ^ 等字符被吞
setlocal DisableDelayedExpansion
call :LOG Step 2: Remove read-only attributes in %APPDIR%
attrib -r ""%APPDIR%\*.*"" /S /D >nul 2>&1
endlocal & set ""rc_copy=ok""

REM ========== 3. 清理旧版本文件（保留 data 目录） ==========
setlocal DisableDelayedExpansion
call :LOG Step 3: Cleaning old files in %APPDIR% (preserve data/ and Update.bat)
REM 先删文件（保留 Update.bat 自身正在运行）
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
endlocal & set ""rc_clean=ok""

REM ========== 4. 复制新文件：robocopy 优先，xcopy 备用 ==========
setlocal DisableDelayedExpansion
call :LOG Step 4: Copying new files from %NEWDIR% to %APPDIR%
set COPYOK=0
if exist ""%SYSTEMROOT%\System32\robocopy.exe"" (
    call :LOG Using robocopy
    ""%SYSTEMROOT%\System32\robocopy.exe"" ""%NEWDIR%"" ""%APPDIR%"" /E /IS /IT /R:3 /W:2 /NFL /NDL /NP /XF Update.bat
    set RC=%ERRORLEVEL%
    if %RC% LSS 8 (
        set COPYOK=1
        call :LOG robocopy OK, code %RC%
    ) else (
        call :LOG robocopy FAILED, code %RC%
    )
)
if ""%COPYOK%""==""0"" (
    call :LOG Using xcopy fallback
    echo Update.bat > ""%APPDIR%\_exclude.tmp"" 2>nul
    xcopy ""%NEWDIR%\*"" ""%APPDIR%"" /E /Y /I /Q /EXCLUDE:%APPDIR%\_exclude.tmp >nul 2>&1
    del /q ""%APPDIR%\_exclude.tmp"" >nul 2>&1
    set RC=%ERRORLEVEL%
    if %RC%==0 (
        set COPYOK=1
        call :LOG xcopy OK.
    ) else (
        call :LOG xcopy FAILED, code %RC%
    )
)
if ""%COPYOK%""==""0"" (
    call :LOG ERROR: Both robocopy and xcopy failed!
    endlocal & goto reporterror
)
endlocal & set ""rc_copyfile=ok""

REM ========== 5. 清理临时文件 ==========
setlocal DisableDelayedExpansion
call :LOG Step 5: Cleaning temp files
rd /s /q ""%NEWDIR%"" >nul 2>&1
del /q ""%ZIPPATH%"" >nul 2>&1
call :LOG Temp cleaned.
endlocal

REM ========== 6. 校验并启动新程序（三种方法依次尝试，每次都用 DisableDelayedExpansion） ==========
setlocal DisableDelayedExpansion
call :LOG Step 6: Verify and launch %EXEPATH%
if not exist ""%EXEPATH%"" (
    call :LOG ERROR: %EXEPATH% does NOT exist after copy!
    goto reporterror
)
REM 获取 exe 文件大小（用 for 循环避免参数引用问题）
for %%A in (""%EXEPATH%"") do call :LOG Exe exists, size=%%~zA bytes.

REM === 启动方法1 ===
call :LOG Launch method 1: cd + start """" (title) /D APPDIR EXEPATH
cd /d ""%APPDIR%""
start """" /D ""%APPDIR%"" ""%EXEPATH%""
set RC=%ERRORLEVEL%
call :LOG start method 1 returned errorlevel=%RC%
timeout /t 3 /nobreak >nul
set ""NEWLAUNCHED=0""
tasklist /NH 2>nul | find /i ""SuixinJi.exe"" >nul
if %errorlevel%==0 (
    set ""NEWLAUNCHED=1""
    call :LOG SUCCESS: New process running after method 1.
)

REM === 启动方法2（方法1失败时） ===
if ""%NEWLAUNCHED%""==""0"" (
    call :LOG WARN: Method 1 failed, trying method 2: start without /D
    cd /d ""%APPDIR%""
    start """" ""%EXEPATH%""
    set RC=%ERRORLEVEL%
    call :LOG Method 2 start returned errorlevel=%RC%
    timeout /t 3 /nobreak >nul
    set ""NEWLAUNCHED=0""
    tasklist /NH 2>nul | find /i ""SuixinJi.exe"" >nul
    if %errorlevel%==0 (
        set ""NEWLAUNCHED=1""
        call :LOG SUCCESS: New process running after method 2.
    )
)

REM === 启动方法3（方法2失败时）：写临时 bat 再用 cmd /c 启动 ===
if ""%NEWLAUNCHED%""==""0"" (
    call :LOG WARN: Method 2 failed, trying method 3: write helper launch bat
    set ""LAUNCHBAT=%APPDIR%\_launch_now.tmp.bat""
    echo @echo off > ""%LAUNCHBAT%""
    echo cd /d ""%APPDIR%"" >> ""%LAUNCHBAT%""
    echo start """" ""%EXEPATH%"" >> ""%LAUNCHBAT%""
    call :LOG Helper bat written: %LAUNCHBAT%
    call ""%LAUNCHBAT%""
    set RC=%ERRORLEVEL%
    call :LOG Helper bat returned %RC%
    timeout /t 4 /nobreak >nul
    set ""NEWLAUNCHED=0""
    tasklist /NH 2>nul | find /i ""SuixinJi.exe"" >nul
    if %errorlevel%==0 (
        call :LOG SUCCESS: New process running after method 3.
    ) else (
        call :LOG ERROR: All 3 launch methods FAILED.
        del /q ""%LAUNCHBAT%"" >nul 2>&1
        goto reporterror
    )
    del /q ""%LAUNCHBAT%"" >nul 2>&1
)
endlocal

call :LOG ===== SuixinJi Update SUCCESS =====
call :LOG Update finished at %date% %time%

REM ========== 7. 删除 Update.bat 自身（延迟删除） ==========
REM 先切到 TEMP，确保 bat 不在 APPDIR 目录内持有句柄
cd /d ""%TEMP%""
start /b "" cmd /c ping -n 4 127.0.0.1 >nul ^& del /f /q ""%BATFILE%""
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
REM 写日志时禁用 delayed expansion，防止路径字符被吞
setlocal DisableDelayedExpansion
echo [%time%] %*
echo [%date% %time%] %* >> ""%LOGFILE%""
endlocal
goto :eof

:RAWLOG
REM 不经过任何处理直接追加原始日志（用于 delayed expansion 切换前）
echo %*
echo %* >> ""%LOGFILE%""
goto :eof
";
        // 占位符替换（不使用 C# 插值 $ 语法，避免编译期解析错误）
        batContent = batContent
            .Replace("__LOGFILE__", logPath)
            .Replace("__APPDIR__", appDir)
            .Replace("__NEWDIR__", newFilesDir)
            .Replace("__EXEPATH__", appExePath)
            .Replace("__ZIPPATH__", tempZipPath)
            .Replace("__PID__", pid.ToString())
            .Replace("__BATFILE__", batPath)
            .Replace("__EXENAME__", exeName);

        // 使用 UTF-8 (无 BOM) 写入 bat，并配合 chcp 65001 切到 UTF-8 代码页，
        // 以正确支持路径中可能出现的中文（如 Windows 用户名为中文）。
        File.WriteAllText(batPath, batContent, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // 关键修复4：不直接启动 bat，而是启动 cmd.exe /K 来跑，
        // 并用 Shell 方式执行，确保 bat 进程与 WPF 主进程完全分离、不随主程序关闭被杀
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/C \"\"{batPath}\"\"",
            WindowStyle = ProcessWindowStyle.Normal,
            CreateNoWindow = false,
            UseShellExecute = true, // Shell 执行会完全独立，父进程退出不受影响
            WorkingDirectory = appDir,
        };

        Process? batProc = null;
        try
        {
            batProc = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: Process.Start(bat) failed: {ex.Message}\n{ex.StackTrace}\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
            throw;
        }

        // 关键修复5：显式等待 bat 进程真的起来（最多等 3 秒），再让主程序关
        if (batProc is not null)
        {
            try
            {
                int waitMs = 0;
                bool batAlive = false;
                while (waitMs < 3000)
                {
                    batProc.Refresh();
                    if (!batProc.HasExited)
                    {
                        batAlive = true;
                        break;
                    }
                    Thread.Sleep(200);
                    waitMs += 200;
                }
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] BAT PROCESS verified: alive={batAlive}, waited={waitMs}ms, batPID={(batProc.HasExited ? "exited" : batProc.Id.ToString())}\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARN: verify bat process exception: {ex.Message}\n",
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }
        }
        else
        {
            try
            {
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARN: Process.Start returned null batProc\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // 写一条更新启动日志，方便排查 bat 没跑的问题
        try
        {
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] C# Updater finished launching. About to exit SuixinJi.exe.\n" +
                $"  接下来的等待旧进程退出、复制文件、启动新程序全部由 Update.bat 独立负责，不再依赖主程序存活。\n\n",
                new System.Text.UTF8Encoding(false));
        }
        catch { }
    }
}
