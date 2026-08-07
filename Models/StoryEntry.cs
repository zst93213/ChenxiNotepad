namespace BlindNotepad.Models;

/// <summary>
/// 话本（小说）条目模型。存储导入的小说内容、章节信息和阅读进度。
/// 数据以 JSON 明文存于 stories.json（不加密，因小说内容通常非敏感数据）。
/// </summary>
[Serializable]
public class StoryEntry
{
    /// <summary>条目唯一标识。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>小说标题。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>作者（可选）。</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    /// <summary>原始文件名。</summary>
    [JsonPropertyName("sourceFileName")]
    public string SourceFileName { get; set; } = "";

    /// <summary>章节列表。</summary>
    [JsonPropertyName("chapters")]
    public List<StoryChapter> Chapters { get; set; } = new();

    /// <summary>当前阅读章节索引（从0开始）。</summary>
    [JsonPropertyName("currentChapterIndex")]
    public int CurrentChapterIndex { get; set; } = 0;

    /// <summary>当前章节内字符位置（从0开始）。</summary>
    [JsonPropertyName("currentCharPosition")]
    public int CurrentCharPosition { get; set; } = 0;

    /// <summary>朗读语速（-5到5，0为正常）。</summary>
    [JsonPropertyName("readingRate")]
    public int ReadingRate { get; set; } = 0;

    /// <summary>是否收藏置顶。</summary>
    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; } = false;

    /// <summary>创建时间。</summary>
    [JsonPropertyName("createdTime")]
    public DateTime CreatedTime { get; set; } = DateTime.Now;

    /// <summary>最后修改时间。</summary>
    [JsonPropertyName("modifiedTime")]
    public DateTime ModifiedTime { get; set; } = DateTime.Now;

    /// <summary>最后阅读时间。</summary>
    [JsonPropertyName("lastReadTime")]
    public DateTime? LastReadTime { get; set; }

    /// <summary>总字数。</summary>
    [JsonPropertyName("totalChars")]
    public int TotalChars { get; set; } = 0;

    /// <summary>阅读进度百分比（0-100）。</summary>
    [JsonPropertyName("progressPercent")]
    public double ProgressPercent { get; set; } = 0;

    /// <summary>更新阅读进度。</summary>
    public void UpdateProgress()
    {
        if (Chapters.Count == 0)
        {
            ProgressPercent = 0;
            return;
        }

        int readChars = 0;
        for (int i = 0; i < CurrentChapterIndex && i < Chapters.Count; i++)
        {
            readChars += Chapters[i].Content.Length;
        }
        readChars += CurrentCharPosition;
        TotalChars = Chapters.Sum(c => c.Content.Length);
        ProgressPercent = TotalChars > 0 ? (double)readChars / TotalChars * 100 : 0;
    }

    /// <summary>获取当前章节。无章节返回null。</summary>
    public StoryChapter? CurrentChapter =>
        CurrentChapterIndex >= 0 && CurrentChapterIndex < Chapters.Count
            ? Chapters[CurrentChapterIndex] : null;
}

/// <summary>
/// 小说章节模型。
/// </summary>
[Serializable]
public class StoryChapter
{
    /// <summary>章节标题。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>章节内容。</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>章节序号（从1开始）。</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; } = 0;
}

/// <summary>
/// 话本集合数据结构（不加密），以 JSON 明文存为 stories.json。
/// </summary>
[Serializable]
public class StoryCollectionData
{
    /// <summary>话本条目列表。</summary>
    [JsonPropertyName("entries")]
    public List<StoryEntry> Entries { get; set; } = new();

    /// <summary>分类列表，默认包含"默认"。</summary>
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new() { "默认" };
}
