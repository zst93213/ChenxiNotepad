using System.Text.Json;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 快捷键配置服务：加载、保存、导出/导入快捷键绑定配置。
/// 配置文件存储于 %LocalAppData%/SuixinJi/shortcuts.json。
/// </summary>
public static class ShortcutConfigService
{
    private static readonly string ShortcutsFilePath = Path.Combine(
        StorageService.AppDataDir, "shortcuts.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>获取默认快捷键绑定列表。</summary>
    public static List<ShortcutBinding> GetDefaultBindings()
    {
        return new List<ShortcutBinding>
        {
            new() { ActionName = "New", DisplayName = "新建", KeyGesture = "Ctrl+N", DefaultGesture = "Ctrl+N", Category = "通用" },
            new() { ActionName = "Save", DisplayName = "保存", KeyGesture = "Ctrl+S", DefaultGesture = "Ctrl+S", Category = "通用" },
            new() { ActionName = "Edit", DisplayName = "编辑", KeyGesture = "Ctrl+E", DefaultGesture = "Ctrl+E", Category = "通用" },
            new() { ActionName = "Delete", DisplayName = "删除", KeyGesture = "Ctrl+D", DefaultGesture = "Ctrl+D", Category = "通用" },
            new() { ActionName = "Search", DisplayName = "搜索", KeyGesture = "Ctrl+F", DefaultGesture = "Ctrl+F", Category = "通用" },
            new() { ActionName = "SwitchUrl", DisplayName = "切换到网址收藏", KeyGesture = "Ctrl+1", DefaultGesture = "Ctrl+1", Category = "导航" },
            new() { ActionName = "SwitchSnippet", DisplayName = "切换到记事本", KeyGesture = "Ctrl+2", DefaultGesture = "Ctrl+2", Category = "导航" },
            new() { ActionName = "SwitchPassword", DisplayName = "切换到密码收藏", KeyGesture = "Ctrl+3", DefaultGesture = "Ctrl+3", Category = "导航" },
            new() { ActionName = "SwitchNote", DisplayName = "切换到日记", KeyGesture = "Ctrl+4", DefaultGesture = "Ctrl+4", Category = "导航" },
            new() { ActionName = "SwitchIdDocument", DisplayName = "切换到证件保存", KeyGesture = "Ctrl+5", DefaultGesture = "Ctrl+5", Category = "导航" },
            new() { ActionName = "SwitchAccounting", DisplayName = "切换到记账", KeyGesture = "Ctrl+6", DefaultGesture = "Ctrl+6", Category = "导航" },
            new() { ActionName = "AccountingStats", DisplayName = "播报当月统计", KeyGesture = "Ctrl+Shift+S", DefaultGesture = "Ctrl+Shift+S", Category = "记账" },
            new() { ActionName = "CopySnippet", DisplayName = "复制笔记内容", KeyGesture = "Ctrl+Shift+C", DefaultGesture = "Ctrl+Shift+C", Category = "记事本" },
            new() { ActionName = "CycleFocus", DisplayName = "切换焦点区域", KeyGesture = "F6", DefaultGesture = "F6", Category = "导航" },
            new() { ActionName = "OpenUrl", DisplayName = "打开网址", KeyGesture = "Enter", DefaultGesture = "Enter", Category = "网址" },
            new() { ActionName = "CopyUrl", DisplayName = "复制网址", KeyGesture = "Ctrl+Enter", DefaultGesture = "Ctrl+Enter", Category = "网址" },
            new() { ActionName = "CopyPassword", DisplayName = "复制密码", KeyGesture = "Ctrl+Shift+C", DefaultGesture = "Ctrl+Shift+C", Category = "密码" },
            new() { ActionName = "CopyUserName", DisplayName = "复制用户名", KeyGesture = "Ctrl+Shift+B", DefaultGesture = "Ctrl+Shift+B", Category = "密码" },
            new() { ActionName = "CopyPhone", DisplayName = "复制手机号", KeyGesture = "Ctrl+Shift+P", DefaultGesture = "Ctrl+Shift+P", Category = "密码" },
            new() { ActionName = "CopyTotp", DisplayName = "复制动态验证码", KeyGesture = "Ctrl+Shift+T", DefaultGesture = "Ctrl+Shift+T", Category = "密码" },
            new() { ActionName = "FieldSelector", DisplayName = "字段选择器", KeyGesture = "Ctrl+Shift+V", DefaultGesture = "Ctrl+Shift+V", Category = "密码" },
            new() { ActionName = "Lock", DisplayName = "锁定密码库", KeyGesture = "Ctrl+Shift+L", DefaultGesture = "Ctrl+Shift+L", Category = "密码" },
            new() { ActionName = "PasswordGenerator", DisplayName = "密码生成器", KeyGesture = "F5", DefaultGesture = "F5", Category = "密码" },
            new() { ActionName = "ToggleFavorite", DisplayName = "切换收藏", KeyGesture = "Ctrl+Shift+F", DefaultGesture = "Ctrl+Shift+F", Category = "通用" },
            new() { ActionName = "ToggleSort", DisplayName = "切换排序", KeyGesture = "Ctrl+Shift+O", DefaultGesture = "Ctrl+Shift+O", Category = "通用" },
            new() { ActionName = "ExportTxt", DisplayName = "导出TXT", KeyGesture = "Ctrl+Shift+X", DefaultGesture = "Ctrl+Shift+X", Category = "通用" },
            new() { ActionName = "FullBackup", DisplayName = "全量备份", KeyGesture = "Ctrl+Shift+A", DefaultGesture = "Ctrl+Shift+A", Category = "通用" },
            new() { ActionName = "FullRestore", DisplayName = "全量恢复", KeyGesture = "Ctrl+Shift+R", DefaultGesture = "Ctrl+Shift+R", Category = "通用" },
            new() { ActionName = "InsertTemplate", DisplayName = "插入模板", KeyGesture = "Ctrl+Shift+I", DefaultGesture = "Ctrl+Shift+I", Category = "记事本" },
            new() { ActionName = "BrowseByMonth", DisplayName = "按月浏览日记", KeyGesture = "Ctrl+Shift+M", DefaultGesture = "Ctrl+Shift+M", Category = "日记" },
        };
    }

    /// <summary>加载快捷键配置。文件不存在时返回默认配置。</summary>
    public static List<ShortcutBinding> Load()
    {
        try
        {
            if (!File.Exists(ShortcutsFilePath))
                return GetDefaultBindings();

            var json = File.ReadAllText(ShortcutsFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return GetDefaultBindings();

            var loaded = JsonSerializer.Deserialize<List<ShortcutBinding>>(json, JsonOptions);
            if (loaded is null || loaded.Count == 0)
                return GetDefaultBindings();

            // 合并默认绑定：如果新增了默认快捷键但配置文件中没有，则补充
            var defaults = GetDefaultBindings();
            foreach (var def in defaults)
            {
                if (!loaded.Any(l => l.ActionName == def.ActionName))
                {
                    loaded.Add(def);
                }
            }

            return loaded;
        }
        catch
        {
            return GetDefaultBindings();
        }
    }

    /// <summary>保存快捷键配置。</summary>
    public static void Save(List<ShortcutBinding> bindings)
    {
        try
        {
            var dir = Path.GetDirectoryName(ShortcutsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(bindings, JsonOptions);
            File.WriteAllText(ShortcutsFilePath, json);
        }
        catch
        {
            // 保存失败时静默处理
        }
    }

    /// <summary>导出快捷键配置到指定路径。</summary>
    public static bool Export(List<ShortcutBinding> bindings, string filePath)
    {
        try
        {
            var json = JsonSerializer.Serialize(bindings, JsonOptions);
            File.WriteAllText(filePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从指定路径导入快捷键配置。</summary>
    public static List<ShortcutBinding>? Import(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<List<ShortcutBinding>>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>重置为默认快捷键配置。</summary>
    public static void ResetToDefault()
    {
        try
        {
            if (File.Exists(ShortcutsFilePath))
            {
                File.Delete(ShortcutsFilePath);
            }
        }
        catch
        {
            // 忽略删除失败
        }
    }

    /// <summary>根据动作名称获取快捷键手势。</summary>
    public static string GetGesture(List<ShortcutBinding> bindings, string actionName)
    {
        var binding = bindings.FirstOrDefault(b => b.ActionName == actionName);
        return binding?.KeyGesture ?? string.Empty;
    }
}
