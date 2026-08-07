namespace BlindNotepad.Models;

/// <summary>
/// 话本（小说/画本）条目模型。存储导入的内容、章节信息和阅读进度。
/// 数据以 JSON 明文存于 stories.json（不加密，因内容通常非敏感数据）。
/// 支持两种格式：纯文本小说（Plain）和画本/广播剧剧本（AudioDrama）。
/// </summary>
[Serializable]
public class StoryEntry
{
    /// <summary>条目唯一标识。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>标题。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>作者（可选）。</summary>
    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    /// <summary>原始文件名。</summary>
    [JsonPropertyName("sourceFileName")]
    public string SourceFileName { get; set; } = "";

    /// <summary>剧本格式：Plain=纯文本小说，AudioDrama=画本/广播剧。</summary>
    [JsonPropertyName("scriptFormat")]
    public ScriptFormat ScriptFormat { get; set; } = ScriptFormat.Plain;

    /// <summary>章节列表。</summary>
    [JsonPropertyName("chapters")]
    public List<StoryChapter> Chapters { get; set; } = new();

    /// <summary>角色列表（仅画本格式有）。</summary>
    [JsonPropertyName("characters")]
    public List<StoryCharacter> Characters { get; set; } = new();

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

    /// <summary>是否画本格式。</summary>
    public bool IsAudioDrama => ScriptFormat == ScriptFormat.AudioDrama;

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
/// 剧本格式类型。
/// </summary>
[Serializable]
public enum ScriptFormat
{
    /// <summary>纯文本小说。</summary>
    Plain = 0,
    /// <summary>画本/广播剧剧本（含角色表和台词标注）。</summary>
    AudioDrama = 1,
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

    /// <summary>章节内的台词行列表（画本格式时使用）。</summary>
    [JsonPropertyName("dialogueLines")]
    public List<DialogueLine> DialogueLines { get; set; } = new();
}

/// <summary>
/// 画本角色模型。
/// </summary>
[Serializable]
public class StoryCharacter
{
    /// <summary>CV名（配音员）。</summary>
    [JsonPropertyName("cvName")]
    public string CvName { get; set; } = "";

    /// <summary>角色名。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>性别：男/女/无。</summary>
    [JsonPropertyName("gender")]
    public string Gender { get; set; } = "";

    /// <summary>角色描述。</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>台词数量。</summary>
    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; } = 0;

    /// <summary>音色类型（如：沉稳磁性音、甜美童音）。</summary>
    [JsonPropertyName("voiceType")]
    public string VoiceType { get; set; } = "";

    /// <summary>年龄段（青年/少年/中年/老年/幼童）。</summary>
    [JsonPropertyName("ageGroup")]
    public string AgeGroup { get; set; } = "";

    /// <summary>是否旁白/后期角色。</summary>
    public bool IsNarrator => Name == "后期" || Name == "旁白" || CvName == "暂无CV";

    /// <summary>是否男性角色。</summary>
    public bool IsMale => Gender == "男";

    /// <summary>是否女性角色。</summary>
    public bool IsFemale => Gender == "女";

    /// <summary>获取显示名（角色名 + CV）。</summary>
    public string DisplayName => $"{Name}" + (string.IsNullOrEmpty(CvName) || CvName == "暂无CV" ? "" : $"（CV: {CvName}）");
}

/// <summary>
/// 台词行模型（画本格式）。
/// </summary>
[Serializable]
public class DialogueLine
{
    /// <summary>行类型：Narration=旁白，Dialogue=台词，SoundEffect=音效，InnerThought=内心独白。</summary>
    [JsonPropertyName("type")]
    public LineType Type { get; set; } = LineType.Narration;

    /// <summary>角色名（台词行才有）。</summary>
    [JsonPropertyName("characterName")]
    public string CharacterName { get; set; } = "";

    /// <summary>CV名（台词行才有）。</summary>
    [JsonPropertyName("cvName")]
    public string CvName { get; set; } = "";

    /// <summary>文本内容。</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>是否内心独白（OS）。</summary>
    [JsonPropertyName("isInnerThought")]
    public bool IsInnerThought { get; set; } = false;
}

/// <summary>
/// 台词行类型。
/// </summary>
[Serializable]
public enum LineType
{
    /// <summary>旁白/叙述。</summary>
    Narration = 0,
    /// <summary>角色台词。</summary>
    Dialogue = 1,
    /// <summary>音效/后期。</summary>
    SoundEffect = 2,
    /// <summary>内心独白。</summary>
    InnerThought = 3,
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
