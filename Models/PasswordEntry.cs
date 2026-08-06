namespace BlindNotepad.Models;

/// <summary>
/// 密码条目模型。整个密码库序列化为 JSON 后加密, 存于 passwords.bnvault。
/// 其中 Password / TotpSecret / SecurityQuestions.Answer / CustomFields(标记 Sensitive) 均为敏感字段。
/// (System / System.Collections.Generic / System.Text.Json.Serialization 由 ImplicitUsings 与 GlobalUsings.cs 提供。)
/// </summary>
[Serializable]
public class PasswordEntry
{
    /// <summary>条目唯一标识。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>平台名称。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>用户名。</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = "";

    /// <summary>密码 (敏感字段)。</summary>
    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    /// <summary>网址。</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>预留手机号。</summary>
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = "";

    /// <summary>预留邮箱。</summary>
    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    /// <summary>TOTP 密钥 (Base32, 敏感字段)。</summary>
    [JsonPropertyName("totpSecret")]
    public string TotpSecret { get; set; } = "";

    /// <summary>备注。</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    /// <summary>标签 (逗号分隔)。</summary>
    [JsonPropertyName("tags")]
    public string Tags { get; set; } = "";

    /// <summary>密保问题列表。</summary>
    [JsonPropertyName("securityQuestions")]
    public List<SecurityQuestion> SecurityQuestions { get; set; } = new();

    /// <summary>自定义字段列表。</summary>
    [JsonPropertyName("customFields")]
    public List<CustomField> CustomFields { get; set; } = new();

    /// <summary>是否收藏置顶。</summary>
    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; } = false;

    /// <summary>上次密码修改时间（用于到期提醒）。</summary>
    [JsonPropertyName("lastPasswordChange")]
    public DateTime LastPasswordChange { get; set; } = DateTime.Now;

    /// <summary>最后修改时间。</summary>
    [JsonPropertyName("modifiedTime")]
    public DateTime ModifiedTime { get; set; } = DateTime.Now;
}
