using System.Security.Cryptography;
using System.Text.Json;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 存储服务: 负责网址收藏 (urls.json, 明文 JSON)、文案收藏 (snippets.json, 明文 JSON) 与密码库
/// (passwords.bnvault, 加密 Base64 文本) 的读写, 以及 RFC 6238 TOTP 生成。
/// 存储目录: %LocalAppData%/BlindNotepad
/// (System / System.Collections.Generic / System.IO 由 ImplicitUsings 提供。)
/// </summary>
public static class StorageService
{
    /// <summary>应用数据根目录。</summary>
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlindNotepad");

    /// <summary>网址数据文件名 (明文 JSON)。</summary>
    public const string UrlsFileName = "urls.json";

    /// <summary>文案收藏数据文件名 (明文 JSON)。</summary>
    public const string SnippetsFileName = "snippets.json";

    /// <summary>密码库文件名 (加密 Base64 文本)。</summary>
    public const string VaultFileName = "passwords.bnvault";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string UrlsFilePath => Path.Combine(AppDataDir, UrlsFileName);
    private static string SnippetsFilePath => Path.Combine(AppDataDir, SnippetsFileName);
    private static string VaultFilePath => Path.Combine(AppDataDir, VaultFileName);

    /// <summary>确保应用数据目录存在。</summary>
    private static void EnsureDirectory()
    {
        if (!Directory.Exists(AppDataDir))
            Directory.CreateDirectory(AppDataDir);
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
        EnsureDirectory();
        string json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(UrlsFilePath, json);
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
        EnsureDirectory();
        string json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(SnippetsFilePath, json);
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
    /// 保存密码库 (Base64 文本)。
    /// </summary>
    public static void SaveVault(string base64Data)
    {
        EnsureDirectory();
        File.WriteAllText(VaultFilePath, base64Data);
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
