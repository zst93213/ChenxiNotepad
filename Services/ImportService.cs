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
    /// 从 Netscape 格式书签 HTML 文件导入网址。
    /// 返回导入的 UrlEntry 列表。
    /// </summary>
    public static List<UrlEntry> ImportBookmarks(string filePath)
    {
        var results = new List<UrlEntry>();

        try
        {
            if (!File.Exists(filePath))
                return results;

            var html = File.ReadAllText(filePath);

            // 匹配 <A HREF="url">title</A> 格式
            var regex = new System.Text.RegularExpressions.Regex(
                @"<A\s+HREF=""([^""]+)""[^>]*>([^<]*)</A>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in regex.Matches(html))
            {
                var url = match.Groups[1].Value.Trim();
                var title = match.Groups[2].Value.Trim();

                if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(title))
                {
                    results.Add(new UrlEntry
                    {
                        Title = title,
                        Url = url,
                        Category = "导入",
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

        return results;
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
