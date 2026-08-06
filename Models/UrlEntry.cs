namespace BlindNotepad.Models;

/// <summary>
/// 网址条目模型。网址数据不加密, 以 JSON 明文存于 urls.json。
/// (System / System.Text.Json.Serialization 由 ImplicitUsings 与 GlobalUsings.cs 提供。)
/// </summary>
[Serializable]
public class UrlEntry
{
    /// <summary>条目唯一标识。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>站点名称 (列表只显示这个)。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>网址。</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>关联的账号/用户名。</summary>
    [JsonPropertyName("account")]
    public string Account { get; set; } = "";

    /// <summary>分类。</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "默认";

    /// <summary>备注。</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    /// <summary>关联的密码条目 ID (可空)。</summary>
    [JsonPropertyName("linkedPasswordId")]
    public string? LinkedPasswordId { get; set; }

    /// <summary>是否收藏置顶。</summary>
    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; } = false;

    /// <summary>上次健康检查时间。</summary>
    [JsonPropertyName("lastCheckedTime")]
    public DateTime? LastCheckedTime { get; set; }

    /// <summary>上次健康检查状态（OK/Failed/Unknown）。</summary>
    [JsonPropertyName("lastCheckStatus")]
    public string LastCheckStatus { get; set; } = "Unknown";

    /// <summary>创建时间。</summary>
    [JsonPropertyName("createdTime")]
    public DateTime CreatedTime { get; set; } = DateTime.Now;

    /// <summary>最后修改时间。</summary>
    [JsonPropertyName("modifiedTime")]
    public DateTime ModifiedTime { get; set; } = DateTime.Now;
}
