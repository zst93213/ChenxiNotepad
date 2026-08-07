using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 话本服务：负责小说/画本导入、自动分章、角色表解析、存储读写和阅读进度管理。
/// 存储目录: %LocalAppData%/SuixinJi/stories.json
/// 支持两种格式：
///   1. 纯文本小说（TXT）- 自动分章
///   2. 画本/广播剧剧本（TXT/DOCX）- 解析角色表、台词标注、旁白、分集
/// </summary>
public static class StoryService
{
    private static readonly string StoriesFilePath = Path.Combine(StorageService.AppDataDir, "stories.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // 章节标题正则模式（支持中文章节标记）
    private static readonly Regex[] ChapterPatterns = new[]
    {
        // 第X章 标题 / 第X回 标题 / 第X节 标题
        new Regex(@"^[\s]*第[零一二三四五六七八九十百千\d]+[章回节卷部篇][\s:：、\.]*(.*)$", RegexOptions.Compiled),
        // 第X集 标题（画本格式）
        new Regex(@"^[\s]*第[零一二三四五六七八九十百千\d]+集[\s:：、\.]*(.*)$", RegexOptions.Compiled),
        // ### 第X集 标题（Markdown画本格式）
        new Regex(@"^[\s]*##+\s*第[零一二三四五六七八九十百千\d]+集[\s:：、\.]*(.*)$", RegexOptions.Compiled),
        // Chapter X 标题
        new Regex(@"^[\s]*Chapter\s+(\d+)[\s:：\.]*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 序章/楔子/引子/前言/后记/尾声/番外
        new Regex(@"^[\s]*(序章|楔子|引子|前言|序言|后记|尾声|番外篇?|终章|终焉|完结章|最终章)[\s:：、\.]*(.*)$", RegexOptions.Compiled),
        // X、标题 （数字+顿号开头）
        new Regex(@"^[\s]*(\d+)[、\.][\s]*(.*)$", RegexOptions.Compiled),
    };

    // 角色表行正则：【CV名】【角色名】【性别】【描述】【台词数】【音色】【年龄段】
    private static readonly Regex CharacterTablePattern = new(
        @"^【([^】]+)】【([^】]+)】【([^】]+)】【([^】*)】【(\d+)】【([^】]*)】【([^】]*)】\s*$",
        RegexOptions.Compiled);

    // 台词行正则：【角色名-CV名】"台词内容" 或 （OS）【角色名-CV名】"台词内容"
    // 匹配各种引号：英文双引号 "、中文双引号 \u201C \u201D
    private static readonly Regex DialoguePattern = new(
        @"^(?:（OS）|\(OS\))?\s*【([^】]+)(?:-([^】]+))?】\s*[\u0022\u201C\u201D\u201E\u201F](.+?)[\u0022\u201C\u201D\u201E\u201F]\s*$",
        RegexOptions.Compiled);

    // 分集标题正则
    private static readonly Regex EpisodePattern = new(
        @"^##+\s*第[零一二三四五六七八九十百千\d]+集.*$",
        RegexOptions.Compiled);

    // 分隔线正则（角色表和正文之间的分隔线）
    private static readonly Regex SeparatorPattern = new(
        @"^[\-\s\\]+$",
        RegexOptions.Compiled);

    /// <summary>从文本文件导入小说/画本，自动检测格式并分章。</summary>
    public static StoryEntry ImportFromFile(string filePath, string encoding = "")
    {
        var content = ReadFileContent(filePath, encoding);
        var title = Path.GetFileNameWithoutExtension(filePath);
        return ImportFromText(title, content);
    }

    /// <summary>从文本内容创建话本，自动检测格式。</summary>
    public static StoryEntry ImportFromText(string title, string content, string author = "")
    {
        // 检测是否为画本格式
        var isAudioDrama = DetectAudioDramaFormat(content);

        StoryEntry entry;
        if (isAudioDrama)
        {
            entry = ParseAudioDramaScript(title, content);
        }
        else
        {
            var chapters = SplitChapters(content);
            entry = new StoryEntry
            {
                Title = title,
                Author = author,
                Chapters = chapters,
                TotalChars = content.Length,
                ScriptFormat = ScriptFormat.Plain,
            };
        }
        entry.UpdateProgress();
        return entry;
    }

    /// <summary>检测文本是否为画本/广播剧格式。</summary>
    private static bool DetectAudioDramaFormat(string content)
    {
        // 检测是否存在角色表行：【CV】【角色名】【性别】【描述】【台词数】【音色】【年龄段】
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int charTableMatches = 0;
        int dialogueMatches = 0;

        foreach (var line in lines.Take(200))
        {
            if (CharacterTablePattern.IsMatch(line.Trim())) charTableMatches++;
            if (DialoguePattern.IsMatch(line.Trim())) dialogueMatches++;
        }

        // 角色表行≥2 或 台词行≥5 判定为画本格式
        return charTableMatches >= 2 || dialogueMatches >= 5;
    }

    /// <summary>解析画本/广播剧剧本。</summary>
    private static StoryEntry ParseAudioDramaScript(string title, string content)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var characters = new List<StoryCharacter>();
        var chapters = new List<StoryChapter>();
        var currentDialogueLines = new List<DialogueLine>();
        var currentContent = new StringBuilder();
        var currentTitle = "";
        int chapterCount = 0;
        bool inCharacterTable = true; // 角色表在文件开头
        bool characterTableEnded = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // 检测角色表结束（分隔线或第一个分集标题）
            if (inCharacterTable && !characterTableEnded)
            {
                if (SeparatorPattern.IsMatch(line) && line.Length > 10)
                {
                    characterTableEnded = true;
                    inCharacterTable = false;
                    continue;
                }
                if (EpisodePattern.IsMatch(line))
                {
                    characterTableEnded = true;
                    inCharacterTable = false;
                    // 不要 continue，继续处理这一行
                }
            }

            // 解析角色表
            if (inCharacterTable && !characterTableEnded)
            {
                var charMatch = CharacterTablePattern.Match(line);
                if (charMatch.Success)
                {
                    characters.Add(new StoryCharacter
                    {
                        CvName = charMatch.Groups[1].Value.Trim(),
                        Name = charMatch.Groups[2].Value.Trim(),
                        Gender = charMatch.Groups[3].Value.Trim(),
                        Description = charMatch.Groups[4].Value.Trim(),
                        LineCount = int.TryParse(charMatch.Groups[5].Value, out var lc) ? lc : 0,
                        VoiceType = charMatch.Groups[6].Value.Trim(),
                        AgeGroup = charMatch.Groups[7].Value.Trim(),
                    });
                    continue;
                }
                // 空行跳过
                if (string.IsNullOrEmpty(line)) continue;
                // 如果不是角色表行也不是空行，角色表可能已结束
                if (!SeparatorPattern.IsMatch(line))
                {
                    characterTableEnded = true;
                    inCharacterTable = false;
                }
                else continue;
            }

            // 检测分集标题
            if (EpisodePattern.IsMatch(line) || IsChapterTitle(line))
            {
                // 保存上一集
                if (currentContent.Length > 0 || currentDialogueLines.Count > 0 || chapterCount > 0)
                {
                    chapterCount++;
                    var ch = new StoryChapter
                    {
                        Title = string.IsNullOrEmpty(currentTitle) ? $"第{chapterCount}集" : currentTitle,
                        Content = currentContent.ToString().TrimEnd(),
                        Index = chapterCount,
                        DialogueLines = new List<DialogueLine>(currentDialogueLines),
                    };
                    chapters.Add(ch);
                    currentContent.Clear();
                    currentDialogueLines.Clear();
                }
                // 提取标题（去掉 ### 前缀）
                currentTitle = line.Replace("#", "").Trim();
                continue;
            }

            // 跳过分隔线
            if (SeparatorPattern.IsMatch(line) && line.Length > 10) continue;
            // 跳过单独的 # 标记
            if (line == "#" || line == @"\#") continue;

            // 解析台词行
            var dialogueMatch = DialoguePattern.Match(line);
            if (dialogueMatch.Success)
            {
                var charName = dialogueMatch.Groups[1].Value.Trim();
                var cvName = dialogueMatch.Groups[2].Success ? dialogueMatch.Groups[2].Value.Trim() : "";
                var text = dialogueMatch.Groups[3].Value.Trim();
                var isOS = line.StartsWith("（OS）") || line.StartsWith("(OS)");

                var dl = new DialogueLine
                {
                    Type = isOS ? LineType.InnerThought : LineType.Dialogue,
                    CharacterName = charName,
                    CvName = cvName,
                    Text = text,
                    IsInnerThought = isOS,
                };
                currentDialogueLines.Add(dl);

                // 同时追加到纯文本内容（用于显示）
                var prefix = isOS ? "（OS）" : "";
                currentContent.AppendLine($"{prefix}【{charName}】\"{text}\"");
            }
            else if (!string.IsNullOrEmpty(line))
            {
                // 旁白/叙述
                currentDialogueLines.Add(new DialogueLine
                {
                    Type = LineType.Narration,
                    Text = line,
                });
                currentContent.AppendLine(line);
            }
            else
            {
                currentContent.AppendLine();
            }
        }

        // 保存最后一集
        chapterCount++;
        if (currentContent.Length > 0 || currentDialogueLines.Count > 0)
        {
            chapters.Add(new StoryChapter
            {
                Title = string.IsNullOrEmpty(currentTitle) ? $"第{chapterCount}集" : currentTitle,
                Content = currentContent.ToString().TrimEnd(),
                Index = chapterCount,
                DialogueLines = new List<DialogueLine>(currentDialogueLines),
            });
        }

        // 如果没有分出章节，全部作为一章
        if (chapters.Count == 0)
        {
            chapters.Add(new StoryChapter { Title = "全文", Content = content, Index = 1 });
        }

        return new StoryEntry
        {
            Title = title,
            Chapters = chapters,
            Characters = characters,
            TotalChars = content.Length,
            ScriptFormat = ScriptFormat.AudioDrama,
        };
    }

    /// <summary>读取文件内容，自动检测编码。</summary>
    private static string ReadFileContent(string filePath, string encoding)
    {
        var bytes = File.ReadAllBytes(filePath);

        // 尝试指定编码
        if (!string.IsNullOrEmpty(encoding))
        {
            try { return Encoding.GetEncoding(encoding).GetString(bytes); }
            catch { }
        }

        // 检测 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 尝试 UTF8，失败则用 GBK
        try
        {
            var utf8 = Encoding.UTF8.GetString(bytes);
            // 如果没有替换字符，说明是合法 UTF8
            if (!utf8.Contains('\uFFFD')) return utf8;
        }
        catch { }

        // GBK / GB2312 / GB18030
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
        catch
        {
            return Encoding.Default.GetString(bytes);
        }
    }

    /// <summary>将文本自动分割为章节。</summary>
    public static List<StoryChapter> SplitChapters(string content)
    {
        if (string.IsNullOrEmpty(content))
            return new List<StoryChapter> { new() { Title = "全文", Content = "", Index = 1 } };

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var chapters = new List<StoryChapter>();
        var currentTitle = "";
        var currentLines = new StringBuilder();

        int chapterCount = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var match = TryMatchChapter(trimmed);

            if (match is not null)
            {
                // 保存上一章
                if (currentLines.Length > 0 || chapterCount > 0)
                {
                    chapterCount++;
                    chapters.Add(new StoryChapter
                    {
                        Title = string.IsNullOrEmpty(currentTitle) ? $"第{chapterCount}节" : currentTitle,
                        Content = currentLines.ToString().TrimEnd(),
                        Index = chapterCount
                    });
                    currentLines.Clear();
                }
                currentTitle = match;
                continue;
            }

            currentLines.AppendLine(line);
        }

        // 保存最后一章
        chapterCount++;
        if (currentLines.Length > 0)
        {
            chapters.Add(new StoryChapter
            {
                Title = string.IsNullOrEmpty(currentTitle) ? $"第{chapterCount}节" : currentTitle,
                Content = currentLines.ToString().TrimEnd(),
                Index = chapterCount
            });
        }

        // 如果没有分出章节，全部作为一章
        if (chapters.Count == 0)
        {
            chapters.Add(new StoryChapter { Title = "全文", Content = content, Index = 1 });
        }

        return chapters;
    }

    /// <summary>尝试匹配章节标题，返回标题文本或null。</summary>
    private static string? TryMatchChapter(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length > 50) return null;

        foreach (var pattern in ChapterPatterns)
        {
            var m = pattern.Match(line);
            if (m.Success)
            {
                var title = line.Trim();
                if (m.Groups.Count > 1 && !string.IsNullOrEmpty(m.Groups[1].Value.Trim()))
                {
                    var suffix = m.Groups[m.Groups.Count - 1].Value.Trim();
                    if (!string.IsNullOrEmpty(suffix))
                        title = $"{m.Groups[0].Value.Trim()}".Trim();
                }
                return title;
            }
        }
        return null;
    }

    /// <summary>判断是否为章节/分集标题行。</summary>
    private static bool IsChapterTitle(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length > 50) return false;
        foreach (var pattern in ChapterPatterns)
        {
            if (pattern.Match(line).Success) return true;
        }
        return false;
    }

    /// <summary>加载话本数据。文件不存在或损坏时返回空数据。</summary>
    public static StoryCollectionData Load()
    {
        try
        {
            if (!File.Exists(StoriesFilePath))
                return new StoryCollectionData();

            var json = File.ReadAllText(StoriesFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return new StoryCollectionData();

            return JsonSerializer.Deserialize<StoryCollectionData>(json, JsonOptions)
                   ?? new StoryCollectionData();
        }
        catch
        {
            return new StoryCollectionData();
        }
    }

    /// <summary>保存话本数据。</summary>
    public static void Save(StoryCollectionData data)
    {
        try
        {
            var dir = Path.GetDirectoryName(StoriesFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(StoriesFilePath, json);
        }
        catch
        {
            // 保存失败静默处理
        }
    }

    /// <summary>将文本按句子分割，用于逐句朗读。</summary>
    public static List<string> SplitSentences(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        var sentences = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            current.Append(ch);
            // 中文句末标点
            if (ch == '。' || ch == '！' || ch == '？' || ch == '…' ||
                ch == '.' || ch == '!' || ch == '?' ||
                ch == '\n' || ch == '\r')
            {
                var s = current.ToString().Trim();
                if (!string.IsNullOrEmpty(s))
                    sentences.Add(s);
                current.Clear();
            }
        }

        var last = current.ToString().Trim();
        if (!string.IsNullOrEmpty(last))
            sentences.Add(last);

        return sentences;
    }

    /// <summary>从DOCX文件读取纯文本内容。</summary>
    public static string ReadDocxContent(string filePath)
    {
        try
        {
            // 使用 System.IO.Compression 读取 docx (ZIP格式)
            using var archive = System.IO.Compression.ZipFile.OpenRead(filePath);
            var docEntry = archive.GetEntry("word/document.xml");
            if (docEntry is null) return "";

            using var stream = docEntry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = reader.ReadToEnd();

            // 提取所有 <w:t> 标签内的文本
            var sb = new StringBuilder();
            var textPattern = new Regex(@"<w:t[^>]*>([^<]*)</w:t>", RegexOptions.Compiled);
            var paraPattern = new Regex(@"<w:p[\s>]", RegexOptions.Compiled);

            // 按 <w:p> 分段，每段提取所有 <w:t> 文本
            var paragraphs = xml.Split(new[] { "</w:p>" }, StringSplitOptions.None);
            foreach (var para in paragraphs)
            {
                var matches = textPattern.Matches(para);
                if (matches.Count > 0)
                {
                    foreach (Match m in matches)
                    {
                        sb.Append(m.Groups[1].Value);
                    }
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>从DOCX文件导入话本。</summary>
    public static StoryEntry ImportFromDocx(string filePath)
    {
        var content = ReadDocxContent(filePath);
        var title = Path.GetFileNameWithoutExtension(filePath);
        return ImportFromText(title, content);
    }
}
