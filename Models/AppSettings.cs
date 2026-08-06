namespace BlindNotepad.Models;

/// <summary>
/// 应用设置。存储自动锁定超时、密码到期周期、防截屏等配置。
/// </summary>
[Serializable]
public class AppSettings
{
    /// <summary>自动锁定超时（分钟），0 表示禁用。默认 5 分钟。</summary>
    [JsonPropertyName("autoLockMinutes")]
    public int AutoLockMinutes { get; set; } = 5;

    /// <summary>密码到期提醒周期（天），0 表示禁用。默认 90 天。</summary>
    [JsonPropertyName("passwordExpiryDays")]
    public int PasswordExpiryDays { get; set; } = 90;

    /// <summary>是否启用防截屏保护。</summary>
    [JsonPropertyName("antiScreenshot")]
    public bool AntiScreenshot { get; set; } = true;

    /// <summary>剪贴板自动清除秒数。</summary>
    [JsonPropertyName("clipboardClearSeconds")]
    public int ClipboardClearSeconds { get; set; } = 30;

    /// <summary>是否启用审计日志。</summary>
    [JsonPropertyName("auditLogEnabled")]
    public bool AuditLogEnabled { get; set; } = true;

    /// <summary>审计日志最大条数。</summary>
    [JsonPropertyName("auditLogMaxEntries")]
    public int AuditLogMaxEntries { get; set; } = 500;
}
