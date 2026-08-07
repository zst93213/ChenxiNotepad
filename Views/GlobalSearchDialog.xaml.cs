using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 全局搜索对话框。跨所有模块（网址/记事本/密码/日记/证件/记账）全文搜索，
/// 支持模块筛选、多字段匹配预览、分组统计。
/// 选中后返回目标模块和条目 ID 供主窗口跳转。
/// 快捷键: Enter 打开选中项, Esc 关闭, 上下键 浏览结果, F3 跳到下一个模块分组。
/// </summary>
public partial class GlobalSearchDialog : Window
{
    private readonly AccessibilityService _a11y = new();

    // 数据源
    private readonly UrlCollectionData _urlData;
    private readonly SnippetCollectionData _snippetData;
    private readonly VaultData? _vault;
    private readonly bool _isUnlocked;

    // 防抖计时器
    private readonly DispatcherTimer _debounceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // 结果上限，避免数据量过大卡顿
    private const int MaxResults = 500;

    /// <summary>搜索结果：选中的目标模块。</summary>
    public string? ResultModule { get; private set; }

    /// <summary>搜索结果：选中的条目 ID。</summary>
    public string? ResultEntryId { get; private set; }

    public GlobalSearchDialog(
        UrlCollectionData urlData,
        SnippetCollectionData snippetData,
        VaultData? vault,
        bool isUnlocked)
    {
        InitializeComponent();
        _urlData = urlData;
        _snippetData = snippetData;
        _vault = vault;
        _isUnlocked = isUnlocked;

        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            PerformSearch();
        };

        // 未解锁时禁用"私密"选项
        if (!_isUnlocked)
        {
            rbPrivate.IsEnabled = false;
        }
    }

    /// <summary>单个搜索结果项。</summary>
    private class SearchResult
    {
        public string ModuleTag { get; set; } = "";
        public string Title { get; set; } = "";
        public string Module { get; set; } = "";
        public string EntryId { get; set; } = "";
        /// <summary>所有匹配字段的预览列表。</summary>
        public List<string> MatchFields { get; set; } = new();
    }

    // ================================================================
    //  搜索触发
    // ================================================================

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        // 防抖：输入时延迟搜索，避免逐字符搜索导致卡顿
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // 模块筛选变化时立即搜索
        if (IsLoaded) PerformSearch();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        searchBox.Clear();
        searchBox.Focus();
        _a11y.Announce(searchBox, "已清除搜索。");
    }

    // ================================================================
    //  核心搜索逻辑
    // ================================================================

    /// <summary>执行跨模块全文搜索。</summary>
    private void PerformSearch()
    {
        var search = (searchBox.Text ?? string.Empty).Trim();
        resultListBox.Items.Clear();

        if (string.IsNullOrEmpty(search))
        {
            statusText.Text = "输入关键词开始搜索";
            return;
        }

        // 公开模块：网址、记事本
        var searchPublic = rbAll.IsChecked == true || rbPublic.IsChecked == true;
        // 私密模块：密码、日记、证件、记账
        var searchPrivate = rbAll.IsChecked == true || rbPrivate.IsChecked == true;

        if (!searchPublic && !searchPrivate)
        {
            statusText.Text = "请至少选择一个搜索范围";
            _a11y.Announce(searchBox, "请至少选择一个搜索范围。");
            return;
        }

        var results = new List<SearchResult>();
        var moduleCounts = new Dictionary<string, int>();
        var lockedSkipped = false;

        // 网址收藏
        if (searchPublic)
        {
            var count = 0;
            foreach (var u in _urlData.Entries)
            {
                if (results.Count >= MaxResults) break;
                var matches = FindAllMatches(search,
                    ("标题", u.Title), ("网址", u.Url), ("账号", u.Account),
                    ("备注", u.Notes), ("分类", u.Category));
                if (matches.Count > 0)
                {
                    results.Add(new SearchResult
                    {
                        ModuleTag = "[网址]", Title = u.Title,
                        Module = "Url", EntryId = u.Id, MatchFields = matches,
                    });
                    count++;
                }
            }
            if (count > 0) moduleCounts["网址"] = count;
        }

        // 记事本
        if (searchPublic)
        {
            var count = 0;
            foreach (var s in _snippetData.Entries)
            {
                if (results.Count >= MaxResults) break;
                var matches = FindAllMatches(search,
                    ("标题", s.Title), ("内容", s.Content), ("分类", s.Category));
                if (matches.Count > 0)
                {
                    results.Add(new SearchResult
                    {
                        ModuleTag = "[记事本]", Title = s.Title,
                        Module = "Snippet", EntryId = s.Id, MatchFields = matches,
                    });
                    count++;
                }
            }
            if (count > 0) moduleCounts["记事本"] = count;
        }

        // 加密模块
        if (_isUnlocked && _vault is not null)
        {
            // 密码收藏
            if (searchPrivate)
            {
                var count = 0;
                foreach (var p in _vault.Entries)
                {
                    if (results.Count >= MaxResults) break;
                    var fields = new List<(string, string)>
                    {
                        ("标题", p.Title), ("用户名", p.UserName), ("网址", p.Url),
                        ("邮箱", p.Email), ("手机号", p.PhoneNumber), ("备注", p.Notes),
                        ("标签", p.Tags),
                    };
                    if (p.CustomFields is not null)
                        foreach (var cf in p.CustomFields)
                            fields.Add((cf.Name, cf.Value));

                    var matches = FindAllMatches(search, fields.ToArray());
                    if (matches.Count > 0)
                    {
                        results.Add(new SearchResult
                        {
                            ModuleTag = "[密码]", Title = p.Title,
                            Module = "Password", EntryId = p.Id, MatchFields = matches,
                        });
                        count++;
                    }
                }
                if (count > 0) moduleCounts["密码"] = count;
            }

            // 日记
            if (searchPrivate)
            {
                var count = 0;
                foreach (var n in _vault.Notes)
                {
                    if (results.Count >= MaxResults) break;
                    var matches = FindAllMatches(search,
                        ("标题", n.Title), ("内容", n.Content),
                        ("天气", n.Weather), ("心情", n.Mood), ("分类", n.Category));
                    if (matches.Count > 0)
                    {
                        results.Add(new SearchResult
                        {
                            ModuleTag = "[日记]", Title = n.Title,
                            Module = "Note", EntryId = n.Id, MatchFields = matches,
                        });
                        count++;
                    }
                }
                if (count > 0) moduleCounts["日记"] = count;
            }

            // 证件保存
            if (searchPrivate)
            {
                var count = 0;
                foreach (var d in _vault.IdDocuments)
                {
                    if (results.Count >= MaxResults) break;
                    var matches = FindAllMatches(search,
                        ("名称", d.Title), ("持有人", d.HolderName), ("号码", d.DocNumber),
                        ("类型", d.DocType), ("签发机关", d.IssueAuthority),
                        ("备注", d.Notes), ("分类", d.Category));
                    if (matches.Count > 0)
                    {
                        results.Add(new SearchResult
                        {
                            ModuleTag = "[证件]", Title = d.Title,
                            Module = "IdDocument", EntryId = d.Id, MatchFields = matches,
                        });
                        count++;
                    }
                }
                if (count > 0) moduleCounts["证件"] = count;
            }

            // 记账
            if (searchPrivate)
            {
                var count = 0;
                foreach (var a in _vault.Accountings)
                {
                    if (results.Count >= MaxResults) break;
                    var matches = FindAllMatches(search,
                        ("说明", a.Title), ("分类", a.Category), ("备注", a.Note),
                        ("支付方式", a.PaymentMethod), ("类型", a.Type));
                    if (matches.Count > 0)
                    {
                        results.Add(new SearchResult
                        {
                            ModuleTag = "[记账]", Title = a.Title,
                            Module = "Accounting", EntryId = a.Id, MatchFields = matches,
                        });
                        count++;
                    }
                }
                if (count > 0) moduleCounts["记账"] = count;
            }
        }
        else
        {
            // 私密模块被选中但未解锁
            if (searchPrivate)
            {
                lockedSkipped = true;
            }
        }

        // 按模块分组排序，使同模块结果聚集在一起
        var moduleOrder = new[] { "Url", "Snippet", "Password", "Note", "IdDocument", "Accounting" };
        results = results.OrderBy(r => Array.IndexOf(moduleOrder, r.Module)).ThenBy(r => r.Title).ToList();

        // 填充列表
        var currentModule = "";
        foreach (var r in results)
        {
            // 模块分组标题
            if (r.Module != currentModule)
            {
                currentModule = r.Module;
                var groupItem = new ListBoxItem
                {
                    Content = $"──── {r.ModuleTag}（{moduleCounts.GetValueOrDefault(r.ModuleTag.Trim('[', ']'))} 条）────",
                    IsEnabled = false,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.Gray,
                };
                resultListBox.Items.Add(groupItem);
            }

            // 结果项：标题 + 所有匹配字段预览
            var display = $"{r.ModuleTag} {r.Title}";
            if (r.MatchFields.Count > 0)
                display += "\n  " + string.Join("  |  ", r.MatchFields);

            var item = new ListBoxItem
            {
                Content = display,
                Tag = r,
            };
            AutomationProperties.SetName(item,
                $"{r.ModuleTag} {r.Title}，匹配字段：{string.Join("，", r.MatchFields)}");
            resultListBox.Items.Add(item);
        }

        // 更新状态栏
        UpdateStatus(results.Count, moduleCounts, lockedSkipped, results.Count >= MaxResults);

        // 选中第一个实际结果项（跳过分组标题）
        SelectFirstResult();
    }

    /// <summary>更新状态栏文本。</summary>
    private void UpdateStatus(int totalCount, Dictionary<string, int> moduleCounts, bool lockedSkipped, bool truncated)
    {
        if (totalCount == 0)
        {
            var msg = "未找到匹配结果";
            if (lockedSkipped)
                msg += "（密码/日记/证件/记账模块未解锁，已跳过）";
            statusText.Text = msg;
            _a11y.Announce(searchBox, msg + "。");
            return;
        }

        var parts = new List<string> { $"共 {totalCount} 条结果" };
        if (moduleCounts.Count > 0)
            parts.Add(string.Join("、", moduleCounts.Select(kv => $"{kv.Key} {kv.Value}")));
        if (truncated)
            parts.Add($"（仅显示前 {MaxResults} 条，请缩小搜索范围）");
        if (lockedSkipped)
            parts.Add("（加密模块未解锁，已跳过）");

        statusText.Text = string.Join("  ", parts);
    }

    /// <summary>选中第一个实际结果项（跳过分组标题）。</summary>
    private void SelectFirstResult()
    {
        for (var i = 0; i < resultListBox.Items.Count; i++)
        {
            if (resultListBox.Items[i] is ListBoxItem item && item.Tag is SearchResult)
            {
                resultListBox.SelectedIndex = i;
                return;
            }
        }
    }

    // ================================================================
    //  匹配逻辑
    // ================================================================

    /// <summary>在多个字段中查找所有匹配项，返回每个匹配字段的预览文本列表。</summary>
    private static List<string> FindAllMatches(string search, params (string Label, string Value)[] fields)
    {
        var result = new List<string>();
        foreach (var (label, value) in fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                var preview = MakePreview(value, search);
                result.Add($"{label}: {preview}");
            }
        }
        return result;
    }

    /// <summary>生成匹配预览：截取匹配位置前后各 30 字符，用【】标记匹配词。</summary>
    private static string MakePreview(string value, string search)
    {
        var idx = value.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return value.Length > 80 ? value[..80] + "…" : value;

        var start = Math.Max(0, idx - 30);
        var end = Math.Min(value.Length, idx + search.Length + 30);
        var preview = value.Substring(start, end - start);
        if (start > 0) preview = "…" + preview;
        if (end < value.Length) preview += "…";
        return preview;
    }

    // ================================================================
    //  结果选择与导航
    // ================================================================

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ConfirmSelection();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        ConfirmSelection();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ResultList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmSelection();
            return;
        }

        // F3 跳到下一个模块分组的第一个结果项
        if (e.Key == Key.F3)
        {
            e.Handled = true;
            JumpToNextGroup();
            return;
        }
    }

    /// <summary>跳到下一个模块分组的第一个实际结果项。</summary>
    private void JumpToNextGroup()
    {
        var currentIdx = resultListBox.SelectedIndex;
        // 找到当前项所属模块
        var currentModule = "";
        if (currentIdx >= 0 && resultListBox.Items[currentIdx] is ListBoxItem curItem && curItem.Tag is SearchResult curResult)
            currentModule = curResult.Module;

        // 向下搜索下一个不同模块的第一个结果项
        for (var i = currentIdx + 1; i < resultListBox.Items.Count; i++)
        {
            if (resultListBox.Items[i] is ListBoxItem item && item.Tag is SearchResult result)
            {
                if (result.Module != currentModule)
                {
                    resultListBox.SelectedIndex = i;
                    resultListBox.ScrollIntoView(item);
                    _a11y.Announce(item, $"已跳转到{result.ModuleTag}分组。");
                    return;
                }
            }
        }

        // 没找到下一个分组，回到第一个结果项
        for (var i = 0; i < resultListBox.Items.Count; i++)
        {
            if (resultListBox.Items[i] is ListBoxItem item && item.Tag is SearchResult)
            {
                resultListBox.SelectedIndex = i;
                resultListBox.ScrollIntoView(item);
                _a11y.Announce(item, "已跳转回第一个结果。");
                return;
            }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        // 在搜索框按 Enter 时，打开当前选中项
        if (e.Key == Key.Enter && searchBox.IsKeyboardFocusWithin && resultListBox.Items.Count > 0)
        {
            e.Handled = true;
            if (resultListBox.SelectedIndex < 0) SelectFirstResult();
            ConfirmSelection();
            return;
        }

        // 在搜索框按上下键时，焦点跳到结果列表
        if (searchBox.IsKeyboardFocusWithin && (e.Key == Key.Down || e.Key == Key.Up) && resultListBox.Items.Count > 0)
        {
            e.Handled = true;
            resultListBox.Focus();
            if (resultListBox.SelectedIndex < 0) SelectFirstResult();
            return;
        }

        // F3 在任意位置都生效
        if (e.Key == Key.F3 && resultListBox.Items.Count > 0)
        {
            e.Handled = true;
            JumpToNextGroup();
            return;
        }
    }

    /// <summary>确认选中结果，设置返回值并关闭对话框。</summary>
    private void ConfirmSelection()
    {
        if (resultListBox.SelectedItem is ListBoxItem item && item.Tag is SearchResult result)
        {
            ResultModule = result.Module;
            ResultEntryId = result.EntryId;
            DialogResult = true;
            Close();
        }
        else
        {
            // 如果当前选中项是分组标题（不可选），尝试找下一个实际结果
            SelectFirstResult();
            if (resultListBox.SelectedItem is ListBoxItem retry && retry.Tag is SearchResult retryResult)
            {
                ResultModule = retryResult.Module;
                ResultEntryId = retryResult.EntryId;
                DialogResult = true;
                Close();
            }
            else
            {
                _a11y.Announce(searchBox, "请先选择一条搜索结果。");
            }
        }
    }
}
