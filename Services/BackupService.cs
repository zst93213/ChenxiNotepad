using System.Text.Json;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 备份与恢复服务：导出加密备份文件，从备份文件恢复。
/// 备份文件格式为 .bnbackup，内容为 Base64 加密数据（与密码库格式相同）。
/// 网址数据导出为明文 JSON（因为本身不加密）。
/// </summary>
public static class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>导出密码库加密备份到指定路径。</summary>
    public static bool ExportVaultBackup(VaultData vault, string masterPassword, string filePath)
    {
        try
        {
            var encrypted = CryptoService.EncryptVault(vault, masterPassword);
            File.WriteAllText(filePath, encrypted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从备份文件恢复密码库。返回解密后的 VaultData，或 null 表示失败。</summary>
    public static VaultData? ImportVaultBackup(string filePath, string masterPassword)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var encrypted = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(encrypted))
                return null;

            return CryptoService.DecryptVault(encrypted, masterPassword);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>导出网址数据到指定路径（明文 JSON）。</summary>
    public static bool ExportUrlBackup(UrlCollectionData urlData, string filePath)
    {
        try
        {
            var json = JsonSerializer.Serialize(urlData, JsonOptions);
            File.WriteAllText(filePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从备份文件恢复网址数据。</summary>
    public static UrlCollectionData? ImportUrlBackup(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<UrlCollectionData>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
