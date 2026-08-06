using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BlindNotepad;

/// <summary>
/// 模板选择对话框。列出可用模板标题，上下光标选择，回车确认，Esc 取消。
/// 确认后通过 <see cref="SelectedTitle"/> 与 <see cref="SelectedContent"/> 返回所选模板。
/// </summary>
public partial class TemplatePickerDialog : Window
{
    private readonly List<(string Title, string Content)> _templates = new();

    /// <summary>选中模板的标题；未选择时为 null。</summary>
    public string? SelectedTitle { get; private set; }

    /// <summary>选中模板的内容；未选择时为 null。</summary>
    public string? SelectedContent { get; private set; }

    public TemplatePickerDialog(IEnumerable<(string Title, string Content)> templates)
    {
        InitializeComponent();

        _templates = templates.ToList();
        PopulateList();

        if (templateList.Items.Count > 0)
        {
            templateList.SelectedIndex = 0;
        }

        Loaded += TemplatePickerDialog_Loaded;
    }

    private void PopulateList()
    {
        foreach (var template in _templates)
        {
            var item = new ListBoxItem { Content = template.Title };
            AutomationProperties.SetName(item, template.Title);
            templateList.Items.Add(item);
        }
    }

    private void TemplatePickerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        templateList.Focus();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = templateList.SelectedIndex;
        if (index >= 0 && index < _templates.Count)
        {
            SelectedTitle = _templates[index].Title;
            SelectedContent = _templates[index].Content;
        }
        else
        {
            SelectedTitle = null;
            SelectedContent = null;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            Close();
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Confirm();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm()
    {
        if (templateList.SelectedIndex < 0)
        {
            return;
        }

        DialogResult = true;
        Close();
    }
}
