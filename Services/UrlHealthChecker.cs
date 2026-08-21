using System.Net.Http;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 网址健康检查服务：异步检测网址是否可访问。
/// 使用 HttpClient 发送 HEAD 请求，超时 10 秒。
/// </summary>
public static class UrlHealthChecker
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>检查单个网址的健康状态。</summary>
    /// <returns>(是否可访问, 状态码或错误信息)</returns>
    public static async Task<(bool Ok, string Status)> CheckUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, "URL 为空");

        try
        {
            var normalized = url.Trim();
            if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "https://" + normalized;
            }

            // 尝试 HEAD 请求，部分服务器不支持 HEAD 则回退到 GET
            try
            {
                var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, normalized));
                return (response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.StatusCode}");
            }
            catch
            {
                var response = await Client.GetAsync(normalized);
                return (response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.StatusCode}");
            }
        }
        catch (TaskCanceledException)
        {
            return (false, "超时");
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>批量检查网址健康状态（串行，适合少量条目）。</summary>
    /// <param name="entries">要检查的网址列表。</param>
    /// <param name="progress">进度回调（当前索引, 总数, 条目）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task CheckAllAsync(
        List<UrlEntry> entries,
        Action<int, int, UrlEntry>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var total = entries.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                entry.LastCheckStatus = "跳过";
                entry.LastCheckedTime = DateTime.Now;
                progress?.Invoke(i + 1, total, entry);
                continue;
            }

            var (ok, status) = await CheckUrlAsync(entry.Url);
            entry.LastCheckStatus = ok ? "OK" : $"失败: {status}";
            entry.LastCheckedTime = DateTime.Now;
            progress?.Invoke(i + 1, total, entry);
        }
    }

    /// <summary>
    /// 批量检查网址健康状态（并发，并发上限 concurrencyLimit，适合大批量导入）。
    /// 对于大书签集合可显著缩短总耗时；每个条目通过取消令牌可提前终止。
    /// </summary>
    public static async Task<int> CheckAllParallelAsync(
        List<UrlEntry> entries,
        IProgress<(int done, int total, UrlEntry entry)>? progress = null,
        int concurrencyLimit = 8,
        CancellationToken cancellationToken = default)
    {
        if (concurrencyLimit < 1) concurrencyLimit = 1;
        var total = entries.Count;
        if (total == 0) return 0;

        var doneCount = 0;
        var active = new List<Task>(concurrencyLimit);
        using var sem = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);

        async Task CheckOne(UrlEntry entry)
        {
            await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry.Url))
                {
                    entry.LastCheckStatus = "跳过";
                    entry.LastCheckedTime = DateTime.Now;
                }
                else
                {
                    var (ok, status) = await CheckUrlAsync(entry.Url).ConfigureAwait(false);
                    entry.LastCheckStatus = ok ? "OK" : $"失败: {status}";
                    entry.LastCheckedTime = DateTime.Now;
                }
            }
            catch (OperationCanceledException)
            {
                entry.LastCheckStatus = "取消";
                throw;
            }
            catch (Exception ex)
            {
                entry.LastCheckStatus = $"异常: {ex.Message}";
            }
            finally
            {
                sem.Release();
                var completed = Interlocked.Increment(ref doneCount);
                progress?.Report((completed, total, entry));
            }
        }

        var tasks = entries.Select(CheckOne).ToList();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 返回已完成数量
        }

        return Volatile.Read(ref doneCount);
    }
}
