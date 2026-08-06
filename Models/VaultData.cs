namespace BlindNotepad.Models;

/// <summary>
/// 整个密码库的数据结构。序列化为 JSON 后加密, 存为 passwords.bnvault。
/// 包含密码条目、记事本条目、审计日志和应用设置。
/// </summary>
[Serializable]
public class VaultData
{
    /// <summary>密码条目列表。</summary>
    [JsonPropertyName("entries")]
    public List<PasswordEntry> Entries { get; set; } = new();

    /// <summary>记事本条目列表（加密存储）。</summary>
    [JsonPropertyName("notes")]
    public List<NoteEntry> Notes { get; set; } = new();

    /// <summary>证件条目列表（加密存储）。</summary>
    [JsonPropertyName("idDocuments")]
    public List<IdDocumentEntry> IdDocuments { get; set; } = new();

    /// <summary>审计日志列表（仅本地存储）。</summary>
    [JsonPropertyName("auditLogs")]
    public List<AuditLogEntry> AuditLogs { get; set; } = new();

    /// <summary>应用设置。</summary>
    [JsonPropertyName("settings")]
    public AppSettings Settings { get; set; } = new();
}

/// <summary>
/// 网址收藏数据结构 (不加密), 以 JSON 明文存为 urls.json。
/// </summary>
[Serializable]
public class UrlCollectionData
{
    /// <summary>网址条目列表。</summary>
    [JsonPropertyName("entries")]
    public List<UrlEntry> Entries { get; set; } = new();

    /// <summary>分类列表, 默认包含 "默认"。</summary>
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new() { "默认" };
}

/// <summary>
/// 文案收藏数据结构 (不加密), 以 JSON 明文存为 snippets.json。
/// </summary>
[Serializable]
public class SnippetCollectionData
{
    /// <summary>文案条目列表。</summary>
    [JsonPropertyName("entries")]
    public List<SnippetEntry> Entries { get; set; } = new();

    /// <summary>分类列表, 默认包含 "默认"。</summary>
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new() { "默认" };
}
