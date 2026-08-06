namespace BlindNotepad.Models;

/// <summary>
/// 日记条目模型。整个日记数据加密存储于密码库中。
/// 新增 Weather（天气）和 Mood（心情）字段。
/// </summary>
[Serializable]
public class NoteEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "默认";

    [JsonPropertyName("weather")]
    public string Weather { get; set; } = "";

    [JsonPropertyName("mood")]
    public string Mood { get; set; } = "";

    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; } = false;

    [JsonPropertyName("createdTime")]
    public DateTime CreatedTime { get; set; } = DateTime.Now;

    [JsonPropertyName("modifiedTime")]
    public DateTime ModifiedTime { get; set; } = DateTime.Now;
}
