using System.Security.Cryptography;
using System.Text.Json;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 存储服务: 负责网址收藏 (urls.json, 明文 JSON)、文案收藏 (snippets.json, 明文 JSON) 与密码库
/// (passwords.bnvault, 加密 Base64 文本) 的读写, 以及 RFC 6238 TOTP 生成。
/// 存储目录: 应用安装目录下的 data/（用户好找；同时保留从旧位置 %LocalAppData%/SuixinJi 自动迁移）。
/// (System / System.Collections.Generic / System.IO 由 ImplicitUsings 提供。)
/// </summary>
public static class StorageService
{
    /// <summary>旧数据目录 (%LocalAppData%/SuixinJi)。首次启动时若存在会自动迁移到新目录。</summary>
    public static readonly string LegacyAppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SuixinJi");

    /// <summary>
    /// 新数据目录 (AppContext.BaseDirectory/data)，位于可执行文件旁边，用户能直接找到。
    /// 若可执行文件目录不可写（例如安装到 Program Files），则回退到 LegacyAppDataDir。
    /// </summary>
    public static readonly string AppDataDir = ResolveAppDataDir();

    /// <summary>
    /// 选择最终数据目录：优先 AppDir/data；若该路径不可写则退回旧目录并输出警告。
    /// </summary>
    private static string ResolveAppDataDir()
    {
        var primary = Path.Combine(UpdateService.AppDir, "data");
        try
        {
            // 可写性测试：确保目录存在并尝试创建一个测试文件后删除
            if (!Directory.Exists(primary))
                Directory.CreateDirectory(primary);
            var probe = Path.Combine(primary, $".write_test_{Guid.NewGuid():N}");
            using (File.Create(probe, 1)) { }
            File.Delete(probe);
            return primary;
        }
        catch
        {
            // 安装在 Program Files 等不可写位置的兜底
            return LegacyAppDataDir;
        }
    }

    /// <summary>
    /// 在首次存取前，若用户已有旧数据目录 (LegacyAppDataDir)，
    /// 把所有已知文件搬到新目录中（同名文件不覆盖以避免丢失新数据）。
    /// </summary>
    public static void MigrateLegacyDataIfNeeded()
    {
        try
        {
            if (AppDataDir == LegacyAppDataDir) return; // 没切换位置，无需迁移
            if (!Directory.Exists(LegacyAppDataDir)) return;

            // ---- 1. 迁移所有已知的独立文件 ----
            var knownFiles = new[]
            {
                UrlsFileName, SnippetsFileName, VaultFileName, VaultBackupFileName,
                "error.log", "settings.json", "audit.json", "drafts.json",
                "shortcuts.json"
            };
            bool migratedAny = false;
            foreach (var name in knownFiles)
            {
                var src = Path.Combine(LegacyAppDataDir, name);
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(AppDataDir, name);
                if (File.Exists(dst)) continue; // 新目录已有则不覆盖
                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir))
                    Directory.CreateDirectory(dstDir);
                File.Copy(src, dst, overwrite: false);
                migratedAny = true;
            }

            // ---- 2. 迁移 drafts/ 子目录（草稿自动保存的文件） ----
            var oldDraftsDir = Path.Combine(LegacyAppDataDir, "drafts");
            var newDraftsDir = Path.Combine(AppDataDir, "drafts");
            if (Directory.Exists(oldDraftsDir))
            {
                try
                {
                    if (!Directory.Exists(newDraftsDir))
                        Directory.CreateDirectory(newDraftsDir);
                    foreach (var draftFile in Directory.GetFiles(oldDraftsDir))
                    {
                        var fileName = Path.GetFileName(draftFile);
                        var dst = Path.Combine(newDraftsDir, fileName);
                        if (!File.Exists(dst))
                        {
                            File.Copy(draftFile, dst, overwrite: false);
                            migratedAny = true;
                        }
                    }
                }
                catch { /* 某个草稿迁移失败不影响整体 */ }
            }

            // ---- 3. 迁移其他已知的子目录：如果将来再加子目录可继续在此扩展 ----

            if (migratedAny)
            {
                // 迁移完成后把旧目录重命名为 .old，避免下次再次迁移
                try { Directory.Move(LegacyAppDataDir, LegacyAppDataDir + ".old"); } catch { }
            }
        }
        catch
        {
            // 迁移失败不阻止程序启动，用户下次手动搬也行
        }
    }

    /// <summary>网址数据文件名 (明文 JSON)。</summary>
    public const string UrlsFileName = "urls.json";

    /// <summary>文案收藏数据文件名 (明文 JSON)。</summary>
    public const string SnippetsFileName = "snippets.json";

    /// <summary>密码库文件名 (加密 Base64 文本)。</summary>
    public const string VaultFileName = "passwords.bnvault";

    /// <summary>密码库自动备份文件名 (保存前的上一版本)。</summary>
    public const string VaultBackupFileName = "passwords.bnvault.bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string UrlsFilePath => Path.Combine(AppDataDir, UrlsFileName);
    private static string SnippetsFilePath => Path.Combine(AppDataDir, SnippetsFileName);
    private static string VaultFilePath => Path.Combine(AppDataDir, VaultFileName);
    private static string VaultBackupFilePath => Path.Combine(AppDataDir, VaultBackupFileName);

    /// <summary>确保应用数据目录存在。</summary>
    private static void EnsureDirectory()
    {
        if (!Directory.Exists(AppDataDir))
            Directory.CreateDirectory(AppDataDir);
    }

    /// <summary>
    /// 原子写入文本文件：先写临时文件，再原子替换目标文件。
    /// 避免写入过程中崩溃/断电导致文件损坏（0 字节或半截）。
    /// </summary>
    private static void WriteAllTextAtomic(string path, string content)
    {
        EnsureDirectory();
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (File.Exists(path))
        {
            // File.Replace 在同卷内是原子操作，且要求目标文件已存在。
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// 加载网址收藏数据。文件不存在或损坏时返回空数据。
    /// </summary>
    public static UrlCollectionData LoadUrls()
    {
        try
        {
            if (!File.Exists(UrlsFilePath))
                return new UrlCollectionData();

            string json = File.ReadAllText(UrlsFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return new UrlCollectionData();

            return JsonSerializer.Deserialize<UrlCollectionData>(json, JsonOptions)
                   ?? new UrlCollectionData();
        }
        catch
        {
            return new UrlCollectionData();
        }
    }

    /// <summary>
    /// 保存网址收藏数据 (明文 JSON)。
    /// </summary>
    public static void SaveUrls(UrlCollectionData data)
    {
        string json = JsonSerializer.Serialize(data, JsonOptions);
        WriteAllTextAtomic(UrlsFilePath, json);
    }

    /// <summary>
    /// 加载文案收藏数据。文件不存在或损坏时返回空数据。
    /// </summary>
    public static SnippetCollectionData LoadSnippets()
    {
        try
        {
            if (!File.Exists(SnippetsFilePath))
                return new SnippetCollectionData();

            string json = File.ReadAllText(SnippetsFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return new SnippetCollectionData();

            return JsonSerializer.Deserialize<SnippetCollectionData>(json, JsonOptions)
                   ?? new SnippetCollectionData();
        }
        catch
        {
            return new SnippetCollectionData();
        }
    }

    /// <summary>
    /// 保存文案收藏数据 (明文 JSON)。
    /// </summary>
    public static void SaveSnippets(SnippetCollectionData data)
    {
        string json = JsonSerializer.Serialize(data, JsonOptions);
        WriteAllTextAtomic(SnippetsFilePath, json);
    }

    /// <summary>
    /// 加载密码库文件内容 (Base64 文本)。文件不存在返回 null。
    /// </summary>
    public static string? LoadVault()
    {
        if (!File.Exists(VaultFilePath))
            return null;

        string content = File.ReadAllText(VaultFilePath);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <summary>
    /// 保存密码库 (Base64 文本)。保存前自动备份当前版本到 .bak 文件。
    /// </summary>
    public static void SaveVault(string base64Data)
    {
        // 保存前备份当前版本，便于误操作后回退
        if (File.Exists(VaultFilePath))
        {
            try { File.Copy(VaultFilePath, VaultBackupFilePath, overwrite: true); }
            catch { /* 备份失败不阻止保存 */ }
        }
        WriteAllTextAtomic(VaultFilePath, base64Data);
    }

    /// <summary>从自动备份恢复密码库文件内容。备份不存在返回 null。</summary>
    public static string? LoadVaultBackup()
    {
        if (!File.Exists(VaultBackupFilePath))
            return null;
        string content = File.ReadAllText(VaultBackupFilePath);
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <summary>判断密码库文件是否已存在。</summary>
    public static bool VaultExists() => File.Exists(VaultFilePath);

    /// <summary>
    /// 生成 RFC 6238 TOTP 验证码: 30 秒步长, 6 位数字, HMAC-SHA1。
    /// secret 采用 Base32 (RFC 4648) 编码, 大小写/空格/连字符/填充符均容错。
    /// </summary>
    /// <param name="secret">Base32 编码的 TOTP 密钥。</param>
    /// <returns>6 位数字验证码; secret 为空或解析失败时返回空字符串。</returns>
    public static string GenerateTotp(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return string.Empty;

        byte[] key = Base32Decode(secret);
        if (key.Length == 0)
            return string.Empty;

        // 计数器 = floor(unix时间 / 30), 转为 8 字节大端序。
        long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        byte[] counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(key);
        byte[] hash = hmac.ComputeHash(counterBytes);

        // 动态截取 (dynamic truncation)。
        int offset = hash[hash.Length - 1] & 0x0F;
        int binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        int otp = binary % 1_000_000;
        return otp.ToString("D6");
    }

    /// <summary>
    /// Base32 (RFC 4648, 无填充) 解码。容错: 大小写不敏感, 忽略空格与连字符, 忽略非法字符与 '='。
    /// </summary>
    private static byte[] Base32Decode(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        string cleaned = input.Trim()
            .ToUpperInvariant()
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .TrimEnd('=');

        int bits = 0;
        int value = 0;
        var result = new List<byte>(cleaned.Length);

        foreach (char c in cleaned)
        {
            int idx = Alphabet.IndexOf(c);
            if (idx < 0)
                continue; // 跳过非法字符

            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return result.ToArray();
    }
}
