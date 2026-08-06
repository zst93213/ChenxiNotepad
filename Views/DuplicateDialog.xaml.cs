using System.Text;
using System.Windows;
using BlindNotepad.Services;

namespace BlindNotepad;

public partial class DuplicateDialog : Window
{
    public DuplicateDialog(
        List<DuplicateDetector.DuplicateUrlGroup> urlGroups,
        List<DuplicateDetector.DuplicatePasswordGroup> passwordGroups)
    {
        InitializeComponent();

        var sb = new StringBuilder();

        if (urlGroups.Count > 0)
        {
            sb.AppendLine("=== 重复网址 ===");
            foreach (var g in urlGroups)
            {
                sb.AppendLine($"URL: {g.Url}（{g.Entries.Count} 条）");
                foreach (var e in g.Entries)
                {
                    sb.AppendLine($"  - {e.Title}（分类: {e.Category}）");
                }
                sb.AppendLine();
            }
        }

        if (passwordGroups.Count > 0)
        {
            sb.AppendLine("=== 重复密码条目 ===");
            foreach (var g in passwordGroups)
            {
                sb.AppendLine($"平台: {g.Title}（{g.Entries.Count} 条）");
                foreach (var e in g.Entries)
                {
                    sb.AppendLine($"  - 用户名: {e.UserName}，修改时间: {e.ModifiedTime:yyyy-MM-dd}");
                }
                sb.AppendLine();
            }
        }

        if (urlGroups.Count == 0 && passwordGroups.Count == 0)
        {
            resultBox.Text = "未检测到重复条目。";
        }
        else
        {
            resultBox.Text = sb.ToString();
        }
    }
}
