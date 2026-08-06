namespace BlindNotepad.Models;

/// <summary>
/// 审计日志条目。记录密码库的解锁、复制、修改等敏感操作（仅本地存储）。
/// </summary>
[Serializable]
public class AuditLogEntry
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.Now;

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; } = true;
}
