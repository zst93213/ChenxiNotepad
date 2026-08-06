using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;
using Microsoft.Win32;

namespace BlindNotepad;

/// <summary>
/// 编辑证件条目的对话框。字段以表格形式垂直排列，纯键盘可操作。
/// 证件图片以 Base64 字符串形式暂存于内存，保存时写入 <see cref="IdDocumentEntry.ImageData"/>。
/// 回车保存（备注框中回车换行，Ctrl+回车保存），Esc 取消。
/// </summary>
public partial class IdDocumentEditDialog : Window
{
    private readonly IdDocumentEntry? _existing;

    // 无障碍服务实例
    private readonly AccessibilityService _a11y = new();

    // 证件图片缓存：读取为 byte[] 后转 Base64，避免在 UI 中持有大对象
    private string? _imageDataBase64;
    private string? _imageFileName;

    /// <summary>编辑结果（点击确定后有效）。</summary>
    public IdDocumentEntry? Result { get; private set; }

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="existing">已有证件条目；为 null 时表示新建。</param>
    /// <param name="categories">可选的分类列表，用于填充分类下拉框。</param>
    public IdDocumentEditDialog(IdDocumentEntry? existing, IReadOnlyList<string> categories)
    {
        InitializeComponent();
        _existing = existing;

        // 证件类型预设选项
        docTypeBox.Items.Add("身份证");
        docTypeBox.Items.Add("驾驶证");
        docTypeBox.Items.Add("护照");
        docTypeBox.Items.Add("港澳通行证");
        docTypeBox.Items.Add("台湾通行证");
        docTypeBox.Items.Add("社保卡");
        docTypeBox.Items.Add("银行卡");
        docTypeBox.Items.Add("其他");

        // 分类选项（去重后填充，保留原始顺序）
        if (categories is not null)
        {
            var seen = new HashSet<string>();
            foreach (var category in categories)
            {
                if (category is not null && seen.Add(category))
                {
                    categoryBox.Items.Add(category);
                }
            }
        }

        if (existing is not null)
        {
            titleBox.Text = existing.Title;
            docTypeBox.Text = existing.DocType;
            docNumberBox.Text = existing.DocNumber;
            holderNameBox.Text = existing.HolderName;
            issueDatePicker.SelectedDate = existing.IssueDate;
            expiryDatePicker.SelectedDate = existing.ExpiryDate;
            issueAuthorityBox.Text = existing.IssueAuthority;
            categoryBox.Text = existing.Category;
            notesBox.Text = existing.Notes;
            _imageDataBase64 = existing.ImageData;
            _imageFileName = existing.ImageFileName;
            UpdateImageFileNameText();
            Title = "编辑证件";
        }
        else
        {
            Title = "新建证件";
            UpdateImageFileNameText();
        }
    }

    // =========================================================================
    // 键盘与保存
    // =========================================================================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var focused = FocusManager.GetFocusedElement(this) as TextBox;
        var inMultiline = focused is not null && focused.AcceptsReturn;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // 单行字段回车保存；备注框中回车换行，Ctrl+回车保存
        if (ctrl || !inMultiline)
        {
            e.Handled = true;
            SaveAndClose();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => SaveAndClose();

    private void SaveAndClose()
    {
        var title = titleBox.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            _a11y.Announce(titleBox, "请输入证件名称。");
            titleBox.Focus();
            return;
        }

        Result = new IdDocumentEntry
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Title = title,
            DocType = docTypeBox.Text?.Trim() ?? string.Empty,
            DocNumber = docNumberBox.Text?.Trim() ?? string.Empty,
            HolderName = holderNameBox.Text?.Trim() ?? string.Empty,
            IssueDate = issueDatePicker.SelectedDate,
            ExpiryDate = expiryDatePicker.SelectedDate,
            IssueAuthority = issueAuthorityBox.Text?.Trim() ?? string.Empty,
            Category = string.IsNullOrWhiteSpace(categoryBox.Text) ? "默认" : categoryBox.Text.Trim(),
            Notes = notesBox.Text ?? string.Empty,
            ImageData = _imageDataBase64,
            ImageFileName = _imageFileName,
            CreatedTime = _existing?.CreatedTime ?? DateTime.Now,
            ModifiedTime = DateTime.Now
        };

        DialogResult = true;
        Close();
    }

    // =========================================================================
    // 证件图片
    // =========================================================================

    private void OnSelectImage(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "选择证件图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|PDF 文件|*.pdf|所有文件|*.*"
        };

        if (ofd.ShowDialog() != true)
        {
            return;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(ofd.FileName);
            _imageDataBase64 = Convert.ToBase64String(bytes);
            _imageFileName = Path.GetFileName(ofd.FileName);
            UpdateImageFileNameText();
            _a11y.Announce(imageFileNameText, $"已选择图片 {_imageFileName}。");
        }
        catch (Exception ex)
        {
            _imageDataBase64 = null;
            _imageFileName = null;
            UpdateImageFileNameText();
            _a11y.Announce(imageFileNameText, "读取图片失败：" + ex.Message);
        }
    }

    private void OnClearImage(object sender, RoutedEventArgs e)
    {
        _imageDataBase64 = null;
        _imageFileName = null;
        UpdateImageFileNameText();
        _a11y.Announce(imageFileNameText, "已清除证件图片。");
    }

    /// <summary>
    /// 更新图片文件名显示：有已选文件时显示文件名，否则显示“未选择”。
    /// </summary>
    private void UpdateImageFileNameText()
    {
        imageFileNameText.Text = string.IsNullOrEmpty(_imageFileName) ? "未选择" : _imageFileName;
    }
}
