namespace BlindNotepad.Models;

/// <summary>
/// 文案收藏条目模型。文案收藏数据不加密, 以 JSON 明文存于 snippets.json。
/// (System / System.Text.Json.Serialization 由 ImplicitUsings 与 GlobalUsings.cs 提供。)
/// </summary>
[Serializable]
public class SnippetEntry
{
    /// <summary>条目唯一标识。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>标题。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>文案内容。</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>分类。</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "默认";

    /// <summary>是否收藏置顶。</summary>
    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; } = false;

    /// <summary>创建时间。</summary>
    [JsonPropertyName("createdTime")]
    public DateTime CreatedTime { get; set; } = DateTime.Now;

    /// <summary>最后修改时间。</summary>
    [JsonPropertyName("modifiedTime")]
    public DateTime ModifiedTime { get; set; } = DateTime.Now;
}
