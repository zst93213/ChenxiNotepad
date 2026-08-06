namespace BlindNotepad.Models;

/// <summary>
/// 快捷键绑定配置。允许用户自定义快捷键映射。
/// </summary>
[Serializable]
public class ShortcutBinding
{
    [JsonPropertyName("actionName")]
    public string ActionName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("keyGesture")]
    public string KeyGesture { get; set; } = "";

    [JsonPropertyName("defaultGesture")]
    public string DefaultGesture { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "通用";
}
