namespace BlindNotepad.Models;

/// <summary>
/// 剪贴板历史条目模型。存储于 clipboard_history.json（明文）。
/// </summary>
[Serializable]
public class ClipboardHistoryEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("preview")]
    public string Preview => Content.Length > 100 ? Content[..100] + "..." : Content;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.Now;

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; } = false;

    [JsonPropertyName("sourceApp")]
    public string SourceApp { get; set; } = "";
}
