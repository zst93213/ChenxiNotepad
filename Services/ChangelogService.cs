namespace BlindNotepad.Services;

/// <summary>
/// 更新日志服务。提供内置的版本更新记录。
/// </summary>
public static class ChangelogService
{
    /// <summary>获取完整更新日志文本。</summary>
    public static string GetFullChangelog()
    {
        return """
            BlindNotepad 更新日志
            ======================

            v1.4 (2026-08-06)
            ─────────────────
            [新增] 记账模块（Ctrl+6）
              - 支持收入/支出记录，预设分类
              - Ctrl+Shift+S 语音播报当月收支统计
              - 详情区显示月度汇总
              - 加密存储，需解锁密码库
            [新增] 全量备份与恢复（Ctrl+Shift+A / Ctrl+Shift+R）
              - 一键导出所有数据为 .bnfull 文件
              - 支持从 .bnfull 文件恢复全部数据
            [优化] 状态栏显示所有模块快捷键提示

            v1.3 (2026-07-20)
            ─────────────────
            [新增] 记事本模块（Ctrl+2，原文案收藏）
              - 10,000 字内容限制
              - 复制内容到剪贴板
            [新增] 日记模块（Ctrl+4，原记事本）
              - 10,000 字内容限制
              - 天气和心情标签
              - 自动日期标题
              - 按月浏览（Ctrl+Shift+M）
              - 连续记录提醒
            [新增] 证件保存模块（Ctrl+5）
              - 加密存储电子证件
              - 支持证件图片
            [新增] 排序模式切换（Ctrl+Shift+O）
            [新增] 导出TXT（Ctrl+Shift+X）
            [新增] 模板插入（Ctrl+Shift+I）
            [新增] 收藏置顶（Ctrl+Shift+F）
            [新增] 批量删除和批量移动分类
            [新增] 自定义快捷键设置
            [新增] 重复检测
            [新增] 网址健康检查
            [新增] 审计日志
            [优化] 网址条目增加账号字段

            v1.2 (2026-06-15)
            ─────────────────
            [新增] 自动锁定（可配置超时时间）
            [新增] 防截屏保护
            [新增] 密码生成器（F5）
            [新增] TOTP 动态验证码（Ctrl+Shift+T）
            [新增] 字段选择器（Ctrl+Shift+V）
            [新增] 导入书签功能
            [新增] 加密备份与恢复
            [优化] 争渡读屏适配优化

            v1.1 (2026-05-10)
            ─────────────────
            [新增] 密码收藏模块（Ctrl+3）
              - AES-256-CBC 加密
              - PBKDF2-SHA256 密钥派生（600,000 次迭代）
              - 密码自动清除剪贴板
            [新增] 网址收藏模块（Ctrl+1）
              - Enter 打开网址
              - Ctrl+Enter 复制网址
            [新增] 分类树管理
            [新增] 全文搜索
            [新增] F6 焦点区域切换
            [优化] 纯键盘操作支持

            v1.0 (2026-04-01)
            ─────────────────
            [初始版本] 面向盲人用户的记事本与密码本
              - WPF + .NET 8 桌面应用
              - UI Automation 适配
              - 争渡读屏兼容
              - JSON 数据存储
            """;
    }

    /// <summary>获取指定版本的更新日志。找不到返回空字符串。</summary>
    public static string GetVersionChangelog(string version)
    {
        var full = GetFullChangelog();
        var marker = $"{version}";
        var idx = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";

        // 找到下一个版本标记
        var nextIdx = full.IndexOf("\n\nv", idx + 1, StringComparison.OrdinalIgnoreCase);
        if (nextIdx < 0)
            return full[idx..].Trim();
        else
            return full[idx..nextIdx].Trim();
    }
}
