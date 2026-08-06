namespace BlindNotepad.Models;

/// <summary>
/// 证件条目模型。整个证件数据序列化为 JSON 后加密, 存于密码库 (passwords.bnvault)。
/// 其中 DocNumber / HolderName / IssueDate / ExpiryDate / ImageData 等均为敏感字段。
/// (System / System.Collections.Generic / System.Text.Json.Serialization 由 ImplicitUsings 与 GlobalUsings.cs 提供。)
/// </summary>
[Serializable]
public class IdDocumentEntry
{
    /// <summary>条目唯一标识。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>证件名称 (如 "身份证"、"驾驶证")。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>证件类型。</summary>
    [JsonPropertyName("docType")]
    public string DocType { get; set; } = "";

    /// <summary>证件号码 (敏感字段)。</summary>
    [JsonPropertyName("docNumber")]
    public string DocNumber { get; set; } = "";

    /// <summary>持有人姓名 (敏感字段)。</summary>
    [JsonPropertyName("holderName")]
    public string HolderName { get; set; } = "";

    /// <summary>签发日期 (可空)。</summary>
    [JsonPropertyName("issueDate")]
    public DateTime? IssueDate { get; set; }

    /// <summary>有效期至 (可空)。</summary>
    [JsonPropertyName("expiryDate")]
    public DateTime? ExpiryDate { get; set; }

    /// <summary>签发机关。</summary>
    [JsonPropertyName("issueAuthority")]
    public string IssueAuthority { get; set; } = "";

    /// <summary>分类。</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "默认";

    /// <summary>备注。</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    /// <summary>证件图片的 Base64 编码 (可空, 敏感字段)。</summary>
    [JsonPropertyName("imageData")]
    public string? ImageData { get; set; }

    /// <summary>原始文件名 (可空)。</summary>
    [JsonPropertyName("imageFileName")]
    public string? ImageFileName { get; set; }

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
