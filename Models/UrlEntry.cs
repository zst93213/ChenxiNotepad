namespace BlindNotepad.Models;

/// <summary>
/// 网址收藏账号条目：一个网址可以有多个账号密码对。
/// </summary>
[Serializable]
public class UrlAccount
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("account")]
    public string Account { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

/// <summary>
/// 网址收藏密钥条目：例如 API Key、二步恢复码等。
/// </summary>
[Serializable]
public class UrlSecret
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("secret")]
    public string Secret { get; set; } = "";
}

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

    /// <summary>
    /// 关联的账号/用户名 (旧版单账号字段, 保留用于向后兼容)。
    /// 新建/编辑时将优先写入 Accounts 列表, 加载时若 Accounts 为空则用此字段迁移。
    /// </summary>
    [JsonPropertyName("account")]
    public string Account { get; set; } = "";

    /// <summary>账号密码列表 (支持多个账号)。</summary>
    [JsonPropertyName("accounts")]
    public List<UrlAccount> Accounts { get; set; } = new();

    /// <summary>密钥列表 (支持多个密钥)。</summary>
    [JsonPropertyName("secrets")]
    public List<UrlSecret> Secrets { get; set; } = new();

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

    /// <summary>
    /// 隐藏字段的 key 集合。
    /// 可用 key:
    ///   - 基础字段: "url", "category", "notes", "linkedPassword", "accounts", "secrets"
    ///   - 单个账号的账号名:   "acc_{id}_account"
    ///   - 单个账号的密码:     "acc_{id}_password"
    ///   - 单个密钥:           "sec_{id}_secret"
    /// 若为空集合表示全部字段显示。
    /// </summary>
    [JsonPropertyName("hiddenFields")]
    public HashSet<string> HiddenFields { get; set; } = new();

    /// <summary>
    /// 加载后调用：把旧版单 Account 字段迁移到 Accounts 列表 (保证向后兼容)。
    /// </summary>
    public void MigrateLegacyAccount()
    {
        if (Accounts.Count == 0 && !string.IsNullOrWhiteSpace(Account))
        {
            Accounts.Add(new UrlAccount { Account = Account.Trim(), Password = "" });
            // 保留 Account 字段不清理, 以免回退版本丢数据
        }
    }
}
