using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 话本服务：负责小说导入、自动分章、存储读写和阅读进度管理。
/// 存储目录: %LocalAppData%/SuixinJi/stories.json
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
        // Chapter X 标题
        new Regex(@"^[\s]*Chapter\s+(\d+)[\s:：\.]*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // 序章/楔子/引子/前言/后记/尾声/番外
        new Regex(@"^[\s]*(序章|楔子|引子|前言|序言|后记|尾声|番外篇?|终章|终焉|完结章|最终章)[\s:：、\.]*(.*)$", RegexOptions.Compiled),
        // X、标题 （数字+顿号开头）
        new Regex(@"^[\s]*(\d+)[、\.][\s]*(.*)$", RegexOptions.Compiled),
    };

    /// <summary>从文本文件导入小说，自动分章。</summary>
    public static StoryEntry ImportFromFile(string filePath, string encoding = "")
    {
        var content = ReadFileContent(filePath, encoding);
        var title = Path.GetFileNameWithoutExtension(filePath);
        var chapters = SplitChapters(content);
        var entry = new StoryEntry
        {
            Title = title,
            SourceFileName = Path.GetFileName(filePath),
            Chapters = chapters,
            TotalChars = content.Length,
        };
        entry.UpdateProgress();
        return entry;
    }

    /// <summary>直接从文本内容创建话本。</summary>
    public static StoryEntry ImportFromText(string title, string content, string author = "")
    {
        var chapters = SplitChapters(content);
        var entry = new StoryEntry
        {
            Title = title,
            Author = author,
            Chapters = chapters,
            TotalChars = content.Length,
        };
        entry.UpdateProgress();
        return entry;
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
                // 完整匹配行作为标题（去掉首尾空白）
                var title = line.Trim();
                // 如果有捕获组，尝试用捕获组拼接
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
}
