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
    /// <remarks>
    /// 对于 PublishSingleFile 自包含发布，AppContext.BaseDirectory 返回的是
    /// exe 文件实际所在目录（不是内部解压的临时目录），可以安全用于文件操作。
    /// </remarks>
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

    /// <summary>比较版本号。返回 true 表示 remote 比 local 新。</summary>
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
    /// </summary>
    public static ReleaseAsset? FindWinX64Asset(ReleaseInfo release)
    {
        if (release.Assets is null || release.Assets.Count == 0) return null;

        var asset = release.Assets.FirstOrDefault(a =>
            a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset is not null) return asset;

        return release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>下载文件到指定路径，报告下载进度。</summary>
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

    /// <summary>解压 zip 到目标目录（覆盖已有文件）。</summary>
    public static void ExtractZip(string zipPath, string destDir)
    {
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, recursive: true);
        Directory.CreateDirectory(destDir);

        ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
    }

    /// <summary>
    /// 执行更新：C# 直接复制文件 + cmd 重启。
    ///
    /// 彻底放弃 bat / PowerShell 脚本方案，改为：
    /// 1. C# 直接在当前进程中完成所有文件复制（不需要外部脚本）
    /// 2. 复制完成后用最简单的 cmd /c 命令启动新 exe
    ///
    /// 这样做的好处：
    /// - 不依赖 PowerShell 执行策略
    /// - 不受代码页 / delayed expansion / 引号转义影响
    /// - 文件复制用 .NET API，可靠且可调试
    /// - 唯一需要外部进程做的就是"启动新 exe"这一步
    /// </summary>
    public static void LaunchUpdater(string newFilesDir, string appDir, string appExePath, string tempZipPath)
    {
        appDir = appDir.TrimEnd('\\', '/');
        newFilesDir = newFilesDir.TrimEnd('\\', '/');
        appExePath = appExePath.TrimEnd('\\', '/');
        tempZipPath = tempZipPath.TrimEnd('\\', '/');

        var exeName = Path.GetFileName(appExePath) ?? "SuixinJi.exe";

        // 自动下钻：zip 解压后可能多一层目录
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

        // 日志
        var updateLogDir = Path.Combine(appDir, "data", "update_logs");
        try { Directory.CreateDirectory(updateLogDir); } catch { }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var logPath = Path.Combine(updateLogDir, $"update_{timestamp}.log");

        void Log(string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            try
            {
                File.AppendAllText(logPath, line + "\n", new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        Log($"===== C# LaunchUpdater Entry =====");
        Log($"  appDir       = {appDir}");
        Log($"  newFilesDir  = {newFilesDir}");
        Log($"  appExePath   = {appExePath}");
        Log($"  tempZipPath  = {tempZipPath}");
        Log($"  currentPID   = {pid}");
        Log($"  newDirHasExe = {File.Exists(Path.Combine(newFilesDir, exeName))}");

        // 生成一个极简的 cmd 脚本，只做两件事：
        // 1. 等待当前进程退出
        // 2. 复制新文件（用 xcopy，因为 robocopy 在某些精简版 Windows 不可用）
        // 3. 启动新 exe
        // 4. 自删除
        //
        // 为什么不用 C# 直接复制？因为当前进程的 exe 文件被锁定，无法覆盖。
        // 必须等当前进程退出后才能复制。所以需要一个独立的外部进程来做这件事。
        //
        // 但这次用最简单的方式：cmd /c 一行命令，不写复杂脚本
        var batPath = Path.Combine(appDir, "_update.cmd");
        var batContent = GetUpdateScript(appDir, newFilesDir, appExePath, tempZipPath, pid, logPath, batPath);
        File.WriteAllText(batPath, batContent, System.Text.Encoding.Default);

        Log($"CMD script written to: {batPath}");

        // 启动 cmd 脚本
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            WindowStyle = ProcessWindowStyle.Normal,
            CreateNoWindow = false,
            UseShellExecute = true,
            WorkingDirectory = appDir,
        };

        try
        {
            var proc = Process.Start(startInfo);
            Log($"cmd process started: {(proc?.Id.ToString() ?? "null")}");

            // 等待确认 cmd 进程存活
            if (proc is not null)
            {
                int waitMs = 0;
                bool cmdAlive = false;
                while (waitMs < 3000)
                {
                    proc.Refresh();
                    if (!proc.HasExited) { cmdAlive = true; break; }
                    Thread.Sleep(200);
                    waitMs += 200;
                }
                Log($"CMD process verified: alive={cmdAlive}, waited={waitMs}ms");
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR: Process.Start failed: {ex.Message}");
            throw;
        }

        Log("C# LaunchUpdater finished. CMD script will handle file copy and restart.");
    }

    /// <summary>
    /// 生成最简化的 cmd 更新脚本。
    /// 极简设计：只有等待→复制→启动→自删除，不做复杂逻辑。
    /// 不使用 delayed expansion，避免特殊字符路径问题。
    /// </summary>
    private static string GetUpdateScript(
        string appDir, string newDir, string exePath, string zipPath,
        int pid, string logPath, string batPath)
    {
        // 使用系统默认编码（ANSI/GBK），cmd 原生支持，不需要 chcp
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine($"REM SuixinJi update script, generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        // 等待旧进程退出：用 goto 循环避免 delayed expansion
        // 每轮等待 2 秒，最多 15 轮 = 30 秒
        sb.AppendLine($"REM Wait for PID {pid} to exit (max 30s)");
        sb.AppendLine($"set WAITS=0");
        sb.AppendLine($":waitloop");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" /NH 2>nul | find \"{pid}\" >nul");
        sb.AppendLine($"if not errorlevel 1 goto stillrunning");
        sb.AppendLine($"goto endwait");
        sb.AppendLine($":stillrunning");
        sb.AppendLine($"set /A WAITS+=1");
        sb.AppendLine($"if %WAITS% GEQ 15 goto forcekill");
        sb.AppendLine($"timeout /t 2 /nobreak >nul");
        sb.AppendLine($"goto waitloop");
        sb.AppendLine($":forcekill");
        sb.AppendLine($"taskkill /F /PID {pid} /T >nul 2>&1");
        sb.AppendLine($"timeout /t 3 /nobreak >nul");
        sb.AppendLine($":endwait");
        sb.AppendLine($"echo Process exited >> \"{logPath}\"");
        sb.AppendLine();
        // 复制新文件（newDir 中不含 data 目录，不会覆盖用户数据）
        sb.AppendLine($"REM Copy new files");
        sb.AppendLine($"echo Copying files... >> \"{logPath}\"");
        sb.AppendLine($"xcopy \"{newDir}\\*\" \"{appDir}\\\" /E /Y /I /Q >nul 2>&1");
        sb.AppendLine($"set COPYRC=%errorlevel%");
        sb.AppendLine($"echo xcopy returned %COPYRC% >> \"{logPath}\"");
        sb.AppendLine();
        // 清理临时文件
        sb.AppendLine($"REM Clean temp");
        sb.AppendLine($"rd /s /q \"{newDir}\" >nul 2>&1");
        sb.AppendLine($"del /q \"{zipPath}\" >nul 2>&1");
        sb.AppendLine();
        // 启动新 exe
        sb.AppendLine($"REM Launch new exe");
        sb.AppendLine($"if exist \"{exePath}\" (");
        sb.AppendLine($"  echo Launching {exePath} >> \"{logPath}\"");
        sb.AppendLine($"  start \"\" \"{exePath}\"");
        sb.AppendLine($"  echo Update SUCCESS >> \"{logPath}\"");
        sb.AppendLine($") else (");
        sb.AppendLine($"  echo ERROR: exe not found >> \"{logPath}\"");
        sb.AppendLine($"  msg * \"Update failed: exe not found. Log: {logPath}\"");
        sb.AppendLine($")");
        sb.AppendLine();
        // 自删除
        sb.AppendLine($"cd /d \"%TEMP%\"");
        sb.AppendLine($"del /f /q \"{batPath}\" >nul 2>&1");
        return sb.ToString();
    }
}
