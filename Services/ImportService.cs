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

    /// <summary>
    /// 从 Netscape 格式书签 HTML 文件导入网址，并保留原书签的文件夹层级结构。
    /// 文件夹通过 &lt;H3&gt; 标题声明、&lt;DL&gt;...&lt;/DL&gt; 包裹子项表达层级。
    /// 导入后的 Category 为路径字符串，以 "/" 分隔（例如 "导入/学习/编程"），
    /// 便于在分类树中按层级展示。无文件夹归属的书签归入 "导入"。
    /// </summary>
    public static List<UrlEntry> ImportBookmarks(string filePath)
    {
        var results = new List<UrlEntry>();

        try
        {
            if (!File.Exists(filePath))
                return results;

            var html = File.ReadAllText(filePath);

            // 按出现顺序匹配四种 token：
            //   1) 文件夹标题 <H3 ...>name</H3>
            //   2) 列表闭合 </DL>
            //   3) 列表开始 <DL ...>
            //   4) 书签 <A HREF="url" ...>title</A>
            // 闭合标签放在开始标签之前，避免 <DL[^>]*> 误匹配。
            var tokenRegex = new System.Text.RegularExpressions.Regex(
                @"<H3[^>]*>(.*?)</H3>|</DL\s*>|<DL[^>]*>|<A\s+[^>]*?HREF\s*=\s*""([^""]+)""[^>]*>(.*?)</A>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Singleline);

            // 文件夹路径栈：每一层可能为 null（根容器，不贡献路径段）
            var folderStack = new List<string?>();
            string? pendingFolder = null;
            const string rootName = "导入";

            foreach (System.Text.RegularExpressions.Match match in tokenRegex.Matches(html))
            {
                var value = match.Value;
                var isClose = value.StartsWith("</DL", StringComparison.OrdinalIgnoreCase);
                var isOpen = !isClose && value.StartsWith("<DL", StringComparison.OrdinalIgnoreCase);
                var isH3 = value.StartsWith("<H3", StringComparison.OrdinalIgnoreCase);
                var isAnchor = value.StartsWith("<A", StringComparison.OrdinalIgnoreCase) && match.Groups[2].Success;

                if (isH3)
                {
                    // 记录待压栈的文件夹名，等遇到下一个 <DL> 时压入
                    pendingFolder = DecodeHtml(match.Groups[1].Value);
                }
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
                    var url = match.Groups[2].Value.Trim();
                    var title = DecodeHtml(match.Groups[3].Value);

                    if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(title))
                    {
                        // 构造完整路径：rootName + 各层非 null 文件夹名
                        var path = new List<string> { rootName };
                        foreach (var f in folderStack)
                            if (!string.IsNullOrEmpty(f)) path.Add(f);

                        results.Add(new UrlEntry
                        {
                            Title = title,
                            Url = url,
                            Category = string.Join("/", path),
                            CreatedTime = DateTime.Now,
                            ModifiedTime = DateTime.Now
                        });
                    }
                }
            }
        }
        catch
        {
            // 导入失败时返回已解析的部分
        }

        return results;
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
