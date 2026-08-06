using System.Text.Json;
using System.Text.Json.Serialization;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 草稿自动保存服务。为记事本和日记模块提供定时草稿保存与恢复。
/// 草稿文件存储于 %LocalAppData%/BlindNotepad/drafts/ 目录。
/// </summary>
public static class DraftService
{
    private static readonly string DraftsDir = Path.Combine(StorageService.AppDataDir, "drafts");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>草稿数据模型。</summary>
    public class DraftData
    {
        [JsonPropertyName("module")]
        public string Module { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("weather")]
        public string Weather { get; set; } = "";

        [JsonPropertyName("mood")]
        public string Mood { get; set; } = "";

        [JsonPropertyName("saveTime")]
        public DateTime SaveTime { get; set; } = DateTime.Now;

        [JsonPropertyName("isNew")]
        public bool IsNew { get; set; } = true;
    }

    private static string GetDraftPath(string moduleKey)
    {
        return Path.Combine(DraftsDir, $"draft_{moduleKey}.json");
    }

    /// <summary>保存草稿。</summary>
    public static void Save(string moduleKey, DraftData draft)
    {
        try
        {
            if (!Directory.Exists(DraftsDir))
                Directory.CreateDirectory(DraftsDir);
            draft.SaveTime = DateTime.Now;
            var json = JsonSerializer.Serialize(draft, JsonOptions);
            File.WriteAllText(GetDraftPath(moduleKey), json);
        }
        catch { }
    }

    /// <summary>加载草稿。无草稿返回 null。</summary>
    public static DraftData? Load(string moduleKey)
    {
        try
        {
            var path = GetDraftPath(moduleKey);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<DraftData>(json, JsonOptions);
        }
        catch { return null; }
    }

    /// <summary>清除草稿。</summary>
    public static void Clear(string moduleKey)
    {
        try
        {
            var path = GetDraftPath(moduleKey);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>检查是否存在草稿。</summary>
    public static bool Exists(string moduleKey)
    {
        return File.Exists(GetDraftPath(moduleKey));
    }
}
