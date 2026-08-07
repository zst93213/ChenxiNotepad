using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlindNotepad.Services;

/// <summary>
/// 应用更新检查服务。通过 GitHub Releases API 检查最新版本。
/// </summary>
public static class UpdateService
{
    public const string RepoOwner = "zst93213";
    public const string RepoName = "BlindNotepad-Releases";
    private const string ApiUrl = "https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>GitHub Release 信息。</summary>
    public class ReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("body")]
        public string Body { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }

    /// <summary>获取当前应用版本号。</summary>
    public static string CurrentVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly();
            var ver = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(ver)) return ver;
            return asm?.GetName().Version?.ToString() ?? "1.0.0";
        }
    }

    /// <summary>从 GitHub 获取最新 Release 信息。失败返回 null。</summary>
    public static async Task<ReleaseInfo?> FetchLatestReleaseAsync(string repoOwner, string repoName)
    {
        try
        {
            var url = ApiUrl.Replace("{Owner}", repoOwner).Replace("{Repo}", repoName);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "BlindNotepad-UpdateCheck");
            req.Headers.Add("Accept", "application/vnd.github+json");

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ReleaseInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>比较版本号。返回 true 表示 remote 比 local 新。</summary>
    public static bool IsNewerVersion(string localVersion, string remoteTag)
    {
        // 去掉 'v' 前缀
        var local = localVersion.TrimStart('v', 'V');
        var remote = remoteTag.TrimStart('v', 'V');

        if (Version.TryParse(local, out var localVer) && Version.TryParse(remote, out var remoteVer))
        {
            return remoteVer > localVer;
        }

        // 降级为字符串比较
        return string.CompareOrdinal(remote, local) > 0;
    }
}
