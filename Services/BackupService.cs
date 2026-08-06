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

    // =====================================================================
    // 全量备份与恢复（.bnfull 格式）
    // =====================================================================

    /// <summary>
    /// 全量备份容器模型。将网址、记事本和密码库打包到一个文件中。
    /// 网址和记事本为明文 JSON，密码库为加密 Base64 字符串。
    /// </summary>
    public class FullBackupData
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("exportTime")]
        public DateTime ExportTime { get; set; } = DateTime.Now;

        [JsonPropertyName("urlData")]
        public string? UrlDataJson { get; set; }

        [JsonPropertyName("snippetData")]
        public string? SnippetDataJson { get; set; }

        /// <summary>加密后的密码库 Base64 文本（与 .bnbackup 格式相同）。null 表示未包含密码库。</summary>
        [JsonPropertyName("vaultEncrypted")]
        public string? VaultEncrypted { get; set; }

        [JsonPropertyName("hasVault")]
        public bool HasVault { get; set; }
    }

    /// <summary>
    /// 全量导出：将网址、记事本和密码库打包为一个 .bnfull 文件。
    /// </summary>
    /// <param name="urlData">网址数据（明文）</param>
    /// <param name="snippetData">记事本数据（明文）</param>
    /// <param name="vault">密码库数据，为 null 则跳过密码库</param>
    /// <param name="masterPassword">主密码，用于加密密码库</param>
    /// <param name="filePath">输出文件路径</param>
    public static bool ExportFullBackup(
        UrlCollectionData urlData,
        SnippetCollectionData snippetData,
        VaultData? vault,
        string? masterPassword,
        string filePath)
    {
        try
        {
            var backup = new FullBackupData
            {
                ExportTime = DateTime.Now,
                UrlDataJson = JsonSerializer.Serialize(urlData, JsonOptions),
                SnippetDataJson = JsonSerializer.Serialize(snippetData, JsonOptions),
            };

            if (vault is not null && !string.IsNullOrEmpty(masterPassword))
            {
                backup.VaultEncrypted = CryptoService.EncryptVault(vault, masterPassword);
                backup.HasVault = true;
            }
            else
            {
                backup.HasVault = false;
            }

            var json = JsonSerializer.Serialize(backup, JsonOptions);
            File.WriteAllText(filePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 全量恢复：从 .bnfull 文件恢复所有数据。
    /// 返回 (urlData, snippetData, vault)，其中 vault 在密码库不可用时为 null。
    /// </summary>
    public static (UrlCollectionData? urlData, SnippetCollectionData? snippetData, VaultData? vault, bool hasVault) ImportFullBackup(
        string filePath,
        string masterPassword)
    {
        try
        {
            if (!File.Exists(filePath))
                return (null, null, null, false);

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
                return (null, null, null, false);

            var backup = JsonSerializer.Deserialize<FullBackupData>(json, JsonOptions);
            if (backup is null)
                return (null, null, null, false);

            UrlCollectionData? urlData = null;
            SnippetCollectionData? snippetData = null;
            VaultData? vault = null;

            if (!string.IsNullOrEmpty(backup.UrlDataJson))
                urlData = JsonSerializer.Deserialize<UrlCollectionData>(backup.UrlDataJson, JsonOptions);

            if (!string.IsNullOrEmpty(backup.SnippetDataJson))
                snippetData = JsonSerializer.Deserialize<SnippetCollectionData>(backup.SnippetDataJson, JsonOptions);

            if (backup.HasVault && !string.IsNullOrEmpty(backup.VaultEncrypted) && !string.IsNullOrEmpty(masterPassword))
                vault = CryptoService.DecryptVault(backup.VaultEncrypted, masterPassword);

            return (urlData, snippetData, vault, backup.HasVault);
        }
        catch
        {
            return (null, null, null, false);
        }
    }
}
