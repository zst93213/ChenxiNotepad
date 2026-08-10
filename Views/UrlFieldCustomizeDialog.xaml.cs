using System.Windows;
using System.Windows.Controls;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 网址条目的字段显示/隐藏配置窗口。
/// 通过保存后立即写回 UrlEntry.HiddenFields 并调用 StorageService.SaveUrls。
/// </summary>
public partial class UrlFieldCustomizeDialog : Window
{
    private readonly UrlEntry _entry;
    private readonly Action _onChanged;   // 配置改变后回调（例如刷新主窗口详情 + 立即保存）

    private static string ChineseIndex(int i) => i switch
    {
        1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五",
        6 => "六", 7 => "七", 8 => "八", 9 => "九", 10 => "十",
        _ => i.ToString()
    };

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="entry">要编辑的条目 (引用会被直接修改)</param>
    /// <param name="onChanged">字段发生变更时的回调 (用于保存+刷新详情)</param>
    public UrlFieldCustomizeDialog(UrlEntry entry, Action onChanged)
    {
        InitializeComponent();
        _entry = entry;
        _onChanged = onChanged;
        RebuildPanel();
    }

    // ------------------------------------------------------------------
    // 生成每一行
    // ------------------------------------------------------------------

    private void AddFieldRow(string fieldKey, string label, string valuePreview)
    {
        var isHidden = _entry.HiddenFields.Contains(fieldKey);

        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 标签
        var lbl = new TextBlock
        {
            Text = label + ":",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Medium
        };
        if (isHidden) lbl.Foreground = SystemColors.GrayTextBrush;
        AutomationProperties.SetName(lbl, label);
        Grid.SetColumn(lbl, 0);
        row.Children.Add(lbl);

        // 内容预览（省略过长文本）
        var preview = string.IsNullOrEmpty(valuePreview) ? "（空）" : valuePreview;
        if (preview.Length > 40) preview = preview.Substring(0, 40) + "...";
        var val = new TextBlock
        {
            Text = preview,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 8, 0)
        };
        if (isHidden) val.Foreground = SystemColors.GrayTextBrush;
        AutomationProperties.SetHelpText(val, $"{label}内容预览");
        Grid.SetColumn(val, 1);
        row.Children.Add(val);

        // 隐藏 / 显示 按钮
        var btn = new Button
        {
            Content = isHidden ? "恢复显示" : "隐藏",
            Padding = new Thickness(12, 3),
            Tag = fieldKey
        };
        AutomationProperties.SetName(btn, isHidden ? $"恢复显示{label}" : $"隐藏{label}");
        btn.Click += OnToggleHideClick;
        Grid.SetColumn(btn, 2);
        row.Children.Add(btn);

        // 分隔线
        var sep = new Border
        {
            Height = 1,
            Background = SystemColors.ControlLightBrush,
            Margin = new Thickness(0, 2, 0, 0)
        };

        fieldsPanel.Children.Add(row);
        fieldsPanel.Children.Add(sep);
    }

    private void OnToggleHideClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;

        var isHiddenNow = _entry.HiddenFields.Contains(key);
        var fieldLabel = ((TextBlock)((Grid)btn.Parent!).Children[0]).Text.Replace(":", "");

        if (!isHiddenNow)
        {
            // 准备隐藏：弹窗确认
            var confirm = MessageBox.Show(
                this,
                $"确定要隐藏字段「{fieldLabel}」吗？\n\n隐藏后该字段在详情区默认不显示（仍可通过详情底部的「显示所有字段」临时查看）。",
                "隐藏字段确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _entry.HiddenFields.Add(key);
        }
        else
        {
            _entry.HiddenFields.Remove(key);
        }

        SaveAndNotify();
        RebuildPanel();
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        if (_entry.HiddenFields.Count == 0)
        {
            MessageBox.Show(this, "当前所有字段都已经是显示状态。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(
            this,
            "确定要恢复显示所有已隐藏的字段吗？",
            "恢复所有字段",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        _entry.HiddenFields.Clear();
        SaveAndNotify();
        RebuildPanel();
    }

    private void SaveAndNotify()
    {
        _entry.ModifiedTime = DateTime.Now;
        // 保存和刷新详情都由调用方（MainWindow）通过回调统一处理，避免跨窗口耦合
        _onChanged?.Invoke();
    }

    // ------------------------------------------------------------------
    // 组装整个列表
    // ------------------------------------------------------------------

    private void RebuildPanel()
    {
        fieldsPanel.Children.Clear();

        // 基础字段
        AddFieldRow("url", "网址", _entry.Url);
        AddFieldRow("category", "分类", _entry.Category);
        AddFieldRow("linkedPassword", "关联密码", _entry.LinkedPasswordId ?? "（无）");
        AddFieldRow("notes", "备注", _entry.Notes);

        // 账号列表
        if (_entry.Accounts.Count > 0)
        {
            var hint = new TextBlock
            {
                Text = "—— 账号密码 ——",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 4)
            };
            fieldsPanel.Children.Add(hint);
        }
        for (int i = 0; i < _entry.Accounts.Count; i++)
        {
            var acc = _entry.Accounts[i];
            var label = $"账号{ChineseIndex(i + 1)}";
            AddFieldRow($"acc_{acc.Id}_account", $"{label}账号", acc.Account);
            AddFieldRow($"acc_{acc.Id}_password", $"{label}密码", acc.Password);
        }

        // 密钥列表
        if (_entry.Secrets.Count > 0)
        {
            var hint = new TextBlock
            {
                Text = "—— 密钥 / API Key ——",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 4)
            };
            fieldsPanel.Children.Add(hint);
        }
        for (int i = 0; i < _entry.Secrets.Count; i++)
        {
            var sec = _entry.Secrets[i];
            AddFieldRow($"sec_{sec.Id}_secret", $"密钥{ChineseIndex(i + 1)}", sec.Secret);
        }

        if (_entry.Accounts.Count == 0 && _entry.Secrets.Count == 0)
        {
            var tip = new TextBlock
            {
                Text = "\n提示：此条目暂未添加账号密码或密钥，编辑条目时可添加。",
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(4)
            };
            fieldsPanel.Children.Add(tip);
        }
    }
}
