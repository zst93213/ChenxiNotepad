using System.Text;
using System.Text.RegularExpressions;

namespace BlindNotepad.Services;

/// <summary>
/// 文本总结与标题提取服务。
/// 基于词频统计和句子权重进行本地文本摘要，无需网络和AI。
/// </summary>
public static class TextSummaryService
{
    // 中文停用词（高频但无意义的词）
    private static readonly HashSet<string> StopWords = new()
    {
        "的","了","是","在","我","有","和","就","不","人","都","一","一个","上","也","很",
        "到","说","要","去","你","会","着","没有","看","好","自己","这","那","他","她",
        "它","们","这个","那个","什么","怎么","为什么","因为","所以","但是","可以","这样",
        "那样","一些","一种","一样","以及","或者","如果","虽然","然后","不过","只是","已经",
        "还是","其实","就是","可能","觉得","应该","现在","以前","以后","时候","地方","东西",
        "事情","一下","一样","这种","那种","这么","那么","为了","对于","关于","通过","根据",
    };

    /// <summary>生成文章摘要。</summary>
    /// <param name="text">原文</param>
    /// <param name="maxSentences">摘要最多包含的句子数，默认3句</param>
    public static string Summarize(string text, int maxSentences = 3)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Length <= 100) return text.Trim();

        // 1. 分句
        var sentences = SplitSentences(text);
        if (sentences.Count <= maxSentences) return string.Join("", sentences);

        // 2. 词频统计
        var wordFreq = ExtractWordFrequency(text);

        // 3. 计算每个句子的权重
        var sentenceScores = new List<(string Sentence, double Score, int Index)>();
        for (var i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            var words = ExtractWords(sentence);
            var score = 0.0;
            foreach (var word in words)
            {
                if (wordFreq.TryGetValue(word, out var freq))
                    score += freq;
            }
            // 归一化：除以句子词数避免长句占优
            score = words.Count > 0 ? score / Math.Sqrt(words.Count) : 0;
            // 位置加权：首段和尾段句子权重更高
            if (i == 0) score *= 1.5;
            else if (i < 3) score *= 1.2;
            else if (i == sentences.Count - 1) score *= 1.1;
            sentenceScores.Add((sentence, score, i));
        }

        // 4. 选取权重最高的句子，按原文顺序排列
        var topSentences = sentenceScores
            .OrderByDescending(s => s.Score)
            .Take(maxSentences)
            .OrderBy(s => s.Index)
            .Select(s => s.Sentence)
            .ToList();

        return string.Join("", topSentences);
    }

    /// <summary>
    /// 提取文章标题/标签建议，适用于公众号、视频号等平台。
    /// 返回多个候选标题供用户选择。
    /// </summary>
    public static List<string> ExtractTitles(string text, string originalTitle = "")
    {
        var titles = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return titles;

        var sentences = SplitSentences(text);
        var wordFreq = ExtractWordFrequency(text);
        var topWords = wordFreq
            .Where(w => !StopWords.Contains(w.Key) && w.Key.Length >= 2)
            .OrderByDescending(w => w.Value)
            .Take(8)
            .Select(w => w.Key)
            .ToList();

        // 1. 基于关键词生成标题
        if (topWords.Count >= 2)
        {
            titles.Add(string.Join("", topWords.Take(3)));
        }
        if (topWords.Count >= 1)
        {
            titles.Add($"关于{topWords[0]}的思考");
        }

        // 2. 提取第一句话作为标题候选（截断到20字）
        if (sentences.Count > 0)
        {
            var firstSentence = sentences[0].Trim();
            if (firstSentence.Length > 4)
            {
                if (firstSentence.Length > 20)
                    firstSentence = firstSentence[..20] + "...";
                titles.Add(firstSentence);
            }
        }

        // 3. 基于高频词生成问句标题
        if (topWords.Count >= 1)
        {
            titles.Add($"{topWords[0]}，你需要知道这些");
            titles.Add($"聊聊{topWords[0]}这件事");
        }

        // 4. 基于原文标题生成变体
        if (!string.IsNullOrEmpty(originalTitle))
        {
            titles.Add($"【{originalTitle}】");
            titles.Add($"深度解析：{originalTitle}");
        }

        // 5. 提取话题标签（#格式）
        if (topWords.Count > 0)
        {
            var tags = topWords.Take(5).Select(w => $"#{w}#");
            titles.Add(string.Join(" ", tags));
        }

        // 去重
        return titles.Distinct().Take(8).ToList();
    }

    /// <summary>
    /// 提取关键词标签（#话题#格式）。
    /// </summary>
    public static List<string> ExtractTags(string text, int maxCount = 5)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        var wordFreq = ExtractWordFrequency(text);
        return wordFreq
            .Where(w => !StopWords.Contains(w.Key) && w.Key.Length >= 2)
            .OrderByDescending(w => w.Value)
            .Take(maxCount)
            .Select(w => $"#{w.Key}#")
            .ToList();
    }

    // ================================================================
    //  内部方法
    // ================================================================

    /// <summary>按句末标点分句。</summary>
    private static List<string> SplitSentences(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        // 按句号、问号、叹号、换行分句
        var parts = Regex.Split(normalized, @"(?<=[。！？\n])");
        return parts
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.Length > 2)
            .ToList();
    }

    /// <summary>提取词语（2-4字的中文词组 + 英文单词）。</summary>
    private static List<string> ExtractWords(string text)
    {
        var words = new List<string>();

        // 提取英文单词
        var engMatches = Regex.Matches(text, @"[A-Za-z]{2,}");
        foreach (Match m in engMatches)
            words.Add(m.Value.ToLowerInvariant());

        // 提取2-4字中文词组（滑动窗口）
        var chineseChars = Regex.Matches(text, @"[\u4e00-\u9fff]");
        if (chineseChars.Count > 0)
        {
            var chars = chineseChars.Cast<Match>().Select(m => m.Value).ToList();
            // 2字词
            for (var i = 0; i < chars.Count - 1; i++)
            {
                var word = chars[i] + chars[i + 1];
                if (!StopWords.Contains(word))
                    words.Add(word);
            }
            // 3字词
            for (var i = 0; i < chars.Count - 2; i++)
            {
                var word = chars[i] + chars[i + 1] + chars[i + 2];
                if (!StopWords.Contains(word))
                    words.Add(word);
            }
        }

        return words;
    }

    /// <summary>统计词频。</summary>
    private static Dictionary<string, int> ExtractWordFrequency(string text)
    {
        var words = ExtractWords(text);
        var freq = new Dictionary<string, int>();
        foreach (var word in words)
        {
            freq.TryGetValue(word, out var count);
            freq[word] = count + 1;
        }
        return freq;
    }
}
