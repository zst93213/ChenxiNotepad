namespace BlindNotepad.Models;

/// <summary>
/// 记账条目。记录每一笔收入或支出，加密存储于密码库中。
/// </summary>
[Serializable]
public class AccountingEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>说明/标题，如"午餐"、"工资"。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>金额（正数）。</summary>
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>类型："收入" 或 "支出"。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "支出";

    /// <summary>分类，如"餐饮"、"交通"、"工资"。</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "其他支出";

    /// <summary>支付方式，如"现金"、"微信"、"支付宝"。</summary>
    [JsonPropertyName("paymentMethod")]
    public string PaymentMethod { get; set; } = "现金";

    /// <summary>交易日期。</summary>
    [JsonPropertyName("date")]
    public DateTime Date { get; set; } = DateTime.Today;

    /// <summary>备注。</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = "";

    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; } = false;

    [JsonPropertyName("createdTime")]
    public DateTime CreatedTime { get; set; } = DateTime.Now;

    [JsonPropertyName("modifiedTime")]
    public DateTime ModifiedTime { get; set; } = DateTime.Now;

    /// <summary>是否为收入。</summary>
    public bool IsIncome => Type == "收入";
}
