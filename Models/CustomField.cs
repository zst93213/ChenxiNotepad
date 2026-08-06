namespace BlindNotepad.Models;

/// <summary>
/// 自定义字段。当 Sensitive 为 true 时, Value 视为敏感字段。
/// </summary>
[Serializable]
public class CustomField
{
    /// <summary>字段名称。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>字段值。</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    /// <summary>是否为敏感字段。</summary>
    [JsonPropertyName("sensitive")]
    public bool Sensitive { get; set; } = false;
}
