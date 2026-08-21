using System.Text.Json;
using System.Xml;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 导入服务：从浏览器书签文件（HTML/Netscape 格式）导入网址，
/// 从 CSV 文件导入密码条目。
/// </summary>
public static class ImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>导入失败的书签项（URL 或标题为空、URL 明显无效等）。</summary>
    public sealed class BookmarkFailure
    {
        /// <summary>原 HTML 中的标题（若能解析到）。</summary>
        public string Title { get; set; } = "";
        /// <summary>原 HTML 中的 URL（若能解析到）。</summary>
        public string Url { get; set; } = "";
        /// <summary>跳过/失败原因，如 "标题为空" "URL 为空"。</summary>
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// 书签导入结构化结果：包含已成功条目、失败条目、原始扫描总数、以及是否全部合并到同一文件夹。
    /// </summary>
    public sealed class BookmarkImportResult
    {
        /// <summary>HTML 中原始扫描到的 &lt;A&gt; 标签总数（包含失败项）。</summary>
        public int TotalScanned { get; set; }
        /// <summary>成功导入的条目数量。</summary>
        public int SuccessCount => SuccessEntries.Count;
        /// <summary>成功导入的条目（带层级或合并后的分类）。</summary>
        public List<UrlEntry> SuccessEntries { get; set; } = new();
        /// <summary>失败/跳过的书签项列表。</summary>
        public List<BookmarkFailure> Failures { get; set; } = new();
        /// <summary>本次使用的根文件夹：若未合并则为 "导入"；若合并则为用户指定路径。</summary>
        public string UsedRootFolder { get; set; } = "导入";
        /// <summary>是否为合并到同一文件夹模式。</summary>
        public bool UsedFlatMode { get; set; }
    }

    /// <summary>
    /// 旧版扁平入口（保留给现有调用方）：
    /// 等价于 ImportBookmarksDetailed(filePath, null) 并返回 SuccessEntries。
    /// </summary>
    public static List<UrlEntry> ImportBookmarks(string filePath)
        => ImportBookmarksDetailed(filePath, null).SuccessEntries;

    /// <summary>
    /// 从 Netscape 格式书签 HTML 文件导入网址，返回结构化结果。
    /// 若 forceFlatFolder 为非空字符串：忽略原书签字典层级，全部归入该文件夹（支持 "/" 分隔的路径）。
    /// 若 forceFlatFolder 为 null：保留原层级，Category 形如 "导入/学习/编程"。
    /// </summary>
    public static BookmarkImportResult ImportBookmarksDetailed(string filePath, string? forceFlatFolder)
    {
        var result = new BookmarkImportResult
        {
            UsedFlatMode = !string.IsNullOrWhiteSpace(forceFlatFolder),
            UsedRootFolder = string.IsNullOrWhiteSpace(forceFlatFolder) ? "导入" : NormalizeFolder(forceFlatFolder),
        };
        var successList = result.SuccessEntries;
        var failuresList = result.Failures;

        try
        {
            if (!File.Exists(filePath))
                return result;

            var html = File.ReadAllText(filePath);

            // 按出现顺序匹配四种 token（闭合标签放在开始标签之前，避免 <DL[^>]*> 误匹配）
            var tokenRegex = new System.Text.RegularExpressions.Regex(
                @"<H3[^>]*>(.*?)</H3>|</DL\s*>|<DL[^>]*>|<A\s+[^>]*?HREF\s*=\s*""([^""]+)""[^>]*>(.*?)</A>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Singleline);

            var folderStack = new List<string?>();
            string? pendingFolder = null;
            var rootName = result.UsedRootFolder;

            foreach (System.Text.RegularExpressions.Match match in tokenRegex.Matches(html))
            {
                var value = match.Value;
                var isClose = value.StartsWith("</DL", StringComparison.OrdinalIgnoreCase);
                var isOpen = !isClose && value.StartsWith("<DL", StringComparison.OrdinalIgnoreCase);
                var isH3 = value.StartsWith("<H3", StringComparison.OrdinalIgnoreCase);
                var isAnchor = value.StartsWith("<A", StringComparison.OrdinalIgnoreCase) && match.Groups[2].Success;

                if (isH3)
                    pendingFolder = DecodeHtml(match.Groups[1].Value);
                else if (isOpen)
                {
                    folderStack.Add(pendingFolder);
                    pendingFolder = null;
                }
                else if (isClose)
                {
                    if (folderStack.Count > 0)
                        folderStack.RemoveAt(folderStack.Count - 1);
                    pendingFolder = null;
                }
                else if (isAnchor)
                {
                    result.TotalScanned++;
                    var url = match.Groups[2].Value.Trim();
                    var title = DecodeHtml(match.Groups[3].Value);

                    if (string.IsNullOrEmpty(url))
                    {
                        failuresList.Add(new BookmarkFailure { Title = title, Url = url, Reason = "URL 为空" });
                        continue;
                    }
                    if (string.IsNullOrEmpty(title))
                    {
                        failuresList.Add(new BookmarkFailure { Title = title, Url = url, Reason = "标题为空" });
                        continue;
                    }

                    string category;
                    if (result.UsedFlatMode)
                    {
                        category = rootName;
                    }
                    else
                    {
                        var path = new List<string> { rootName };
                        foreach (var f in folderStack)
                            if (!string.IsNullOrEmpty(f)) path.Add(f);
                        category = string.Join("/", path);
                    }

                    successList.Add(new UrlEntry
                    {
                        Title = title,
                        Url = url,
                        Category = category,
                        CreatedTime = DateTime.Now,
                        ModifiedTime = DateTime.Now
                    });
                }
            }
        }
        catch
        {
            // 导入失败时返回已解析的部分
        }

        return result;
    }

    /// <summary>规范化文件夹路径：去除首尾空白和多余 '/'。</summary>
    private static string NormalizeFolder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "导入";
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "导入";
        return string.Join("/", parts);
    }

    /// <summary>解码 Netscape 书签 HTML 中的常见实体并去除首尾空白。</summary>
    private static string DecodeHtml(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'")
                .Replace("&nbsp;", " ")
                .Trim();
    }

    /// <summary>
    /// 从 CSV 文件导入密码条目。
    /// CSV 格式：title,userName,password,url,phoneNumber,email,notes
    /// 第一行为表头，跳过。
    /// </summary>
    public static List<PasswordEntry> ImportPasswordsFromCsv(string filePath)
    {
        var results = new List<PasswordEntry>();

        try
        {
            if (!File.Exists(filePath))
                return results;

            var lines = File.ReadAllLines(filePath);
            if (lines.Length < 2)
                return results;

            // 跳过表头，从第二行开始
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                var fields = ParseCsvLine(line);
                if (fields.Count == 0)
                    continue;

                var entry = new PasswordEntry
                {
                    Title = fields.Count > 0 ? fields[0] : "",
                    UserName = fields.Count > 1 ? fields[1] : "",
                    Password = fields.Count > 2 ? fields[2] : "",
                    Url = fields.Count > 3 ? fields[3] : "",
                    PhoneNumber = fields.Count > 4 ? fields[4] : "",
                    Email = fields.Count > 5 ? fields[5] : "",
                    Notes = fields.Count > 6 ? fields[6] : "",
                    LastPasswordChange = DateTime.Now,
                    ModifiedTime = DateTime.Now
                };

                if (!string.IsNullOrEmpty(entry.Title))
                {
                    results.Add(entry);
                }
            }
        }
        catch
        {
            // 导入失败时返回已解析的部分
        }

        return results;
    }

    /// <summary>解析 CSV 行，支持双引号包裹的字段。</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }
}
