using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 重复检测服务：检测重复的网址和相同平台重复的密码条目。
/// </summary>
public static class DuplicateDetector
{
    /// <summary>重复网址检测结果。</summary>
    public class DuplicateUrlGroup
    {
        public string Url { get; set; } = "";
        public List<UrlEntry> Entries { get; set; } = new();
    }

    /// <summary>重复密码检测结果。</summary>
    public class DuplicatePasswordGroup
    {
        public string Title { get; set; } = "";
        public List<PasswordEntry> Entries { get; set; } = new();
    }

    /// <summary>检测重复的网址条目（URL 相同视为重复，忽略大小写和尾部斜杠）。</summary>
    public static List<DuplicateUrlGroup> DetectDuplicateUrls(List<UrlEntry> entries)
    {
        var groups = new List<DuplicateUrlGroup>();

        var normalized = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Url))
            .GroupBy(e => NormalizeUrl(e.Url), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in normalized)
        {
            groups.Add(new DuplicateUrlGroup
            {
                Url = group.Key,
                Entries = group.ToList()
            });
        }

        return groups;
    }

    /// <summary>检测重复的密码条目（平台名称相同视为重复，忽略大小写）。</summary>
    public static List<DuplicatePasswordGroup> DetectDuplicatePasswords(List<PasswordEntry> entries)
    {
        var groups = new List<DuplicatePasswordGroup>();

        var normalized = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Title))
            .GroupBy(e => e.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in normalized)
        {
            groups.Add(new DuplicatePasswordGroup
            {
                Title = group.Key,
                Entries = group.ToList()
            });
        }

        return groups;
    }

    /// <summary>标准化 URL：去除协议前缀、转为小写、去除尾部斜杠。</summary>
    private static string NormalizeUrl(string url)
    {
        var normalized = url.Trim().ToLowerInvariant();
        if (normalized.StartsWith("https://"))
            normalized = normalized["https://".Length..];
        else if (normalized.StartsWith("http://"))
            normalized = normalized["http://".Length..];

        return normalized.TrimEnd('/');
    }

    /// <summary>检测是否有重复（网址或密码）。</summary>
    public static bool HasDuplicates(List<UrlEntry> urlEntries, List<PasswordEntry>? passwordEntries)
    {
        return DetectDuplicateUrls(urlEntries).Count > 0
               || (passwordEntries is not null && DetectDuplicatePasswords(passwordEntries).Count > 0);
    }
}
