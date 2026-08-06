namespace BlindNotepad.Models;

/// <summary>
/// 密保问题。Answer 为敏感字段。
/// </summary>
[Serializable]
public class SecurityQuestion
{
    /// <summary>密保问题。</summary>
    [JsonPropertyName("question")]
    public string Question { get; set; } = "";

    /// <summary>密保答案 (敏感字段)。</summary>
    [JsonPropertyName("answer")]
    public string Answer { get; set; } = "";
}
