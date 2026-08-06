using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 审计日志服务：记录密码库的解锁、复制、修改等敏感操作（仅本地存储于加密的密码库中）。
/// </summary>
public static class AuditLogService
{
    /// <summary>记录一条审计日志。如果审计功能未启用则跳过。</summary>
    public static void Log(VaultData? vault, string action, string detail, bool success = true)
    {
        if (vault is null || !vault.Settings.AuditLogEnabled)
            return;

        var entry = new AuditLogEntry
        {
            Timestamp = DateTime.Now,
            Action = action,
            Detail = detail,
            Success = success
        };

        vault.AuditLogs.Add(entry);

        // 超过最大条数时移除最早的记录
        while (vault.AuditLogs.Count > vault.Settings.AuditLogMaxEntries)
        {
            vault.AuditLogs.RemoveAt(0);
        }
    }

    /// <summary>清空所有审计日志。</summary>
    public static void Clear(VaultData? vault)
    {
        if (vault is not null)
        {
            vault.AuditLogs.Clear();
        }
    }

    /// <summary>获取审计日志的摘要文本（用于显示）。</summary>
    public static string GetSummary(VaultData? vault, int maxEntries = 50)
    {
        if (vault is null || vault.AuditLogs.Count == 0)
            return "暂无审计日志。";

        var logs = vault.AuditLogs
            .Skip(Math.Max(0, vault.AuditLogs.Count - maxEntries))
            .Reverse();

        var lines = new System.Text.StringBuilder();
        foreach (var log in logs)
        {
            var status = log.Success ? "成功" : "失败";
            lines.AppendLine($"[{log.Timestamp:yyyy-MM-dd HH:mm:ss}] {log.Action} - {log.Detail} ({status})");
        }

        return lines.ToString();
    }
}
