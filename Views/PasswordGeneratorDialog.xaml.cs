using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 密码生成器对话框。
/// 可配置长度（8-40）、大小写、数字、符号、易读模式；生成后以文本显示强度（弱/中/强）。
/// 生成结果默认用 PasswordBox 掩码显示，可按“显示”临时查看明文 5 秒。确定后返回生成的密码。
/// </summary>
public partial class PasswordGeneratorDialog : Window
{
    private static readonly string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private static readonly string UpperReadable = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // 排除 I、O
    private static readonly string Lower = "abcdefghijklmnopqrstuvwxyz";
    private static readonly string LowerReadable = "abcdefghijkmnpqrstuvwxyz"; // 排除 l、o
    private static readonly string Digits = "0123456789";
    private static readonly string DigitsReadable = "23456789"; // 排除 0、1
    private static readonly string Symbols = "!@#$%^&*()-_=+[]{};:,.?";

    private string _generatedPassword = string.Empty;
    private DispatcherTimer? _revealTimer;

    // 无障碍服务实例
    private readonly AccessibilityService _a11y = new();

    /// <summary>生成的密码（点击确定后供调用方读取）。</summary>
    public string GeneratedPassword => _generatedPassword;

    public PasswordGeneratorDialog()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Regenerate(announce: false);
        _a11y.Announce(generateButton, "密码生成器，已生成一个密码。按空格重新生成，按回车确认使用。");
        generateButton.Focus();
    }

    // =========================================================================
    // 选项变化
    // =========================================================================

    private void OnLengthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        lengthValueText.Text = ((int)lengthSlider.Value).ToString();
        if (IsLoaded)
        {
            Regenerate(announce: false);
        }
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            Regenerate(announce: false);
        }
    }

    private void OnGenerate(object sender, RoutedEventArgs e) => Regenerate(announce: true);

    // =========================================================================
    // 生成与强度
    // =========================================================================

    private void Regenerate(bool announce)
    {
        var length = (int)lengthSlider.Value;
        var readable = readableCheck.IsChecked == true;

        var sets = new List<string>();
        if (upperCheck.IsChecked == true) sets.Add(readable ? UpperReadable : Upper);
        if (lowerCheck.IsChecked == true) sets.Add(readable ? LowerReadable : Lower);
        if (digitCheck.IsChecked == true) sets.Add(readable ? DigitsReadable : Digits);
        if (symbolCheck.IsChecked == true) sets.Add(Symbols);

        // 至少启用一种字符集，否则回退为小写字母
        if (sets.Count == 0)
        {
            sets.Add(readable ? LowerReadable : Lower);
            lowerCheck.IsChecked = true;
        }

        var pool = string.Concat(sets);
        var chars = new char[length];

        // 先保证每个启用的字符集至少出现一个，再随机填充
        for (var i = 0; i < sets.Count && i < length; i++)
        {
            chars[i] = PickChar(sets[i]);
        }

        for (var i = sets.Count; i < length; i++)
        {
            chars[i] = PickChar(pool);
        }

        // Fisher-Yates 洗牌
        for (var i = length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        _generatedPassword = new string(chars);
        resultBox.Password = _generatedPassword;
        revealBox.Text = _generatedPassword;

        var strength = ComputeStrength(length, sets.Count);
        strengthText.Text = $"强度：{strength}";

        if (announce)
        {
            _a11y.Announce(strengthText, $"已生成密码，强度：{strength}。");
        }
    }

    private static char PickChar(string set)
    {
        return set[RandomNumberGenerator.GetInt32(set.Length)];
    }

    /// <summary>根据长度与字符集数量估算强度（弱/中/强）。</summary>
    private static string ComputeStrength(int length, int charsetCount)
    {
        if (length < 10 || charsetCount <= 1)
        {
            return "弱";
        }

        if (length < 16 || charsetCount == 2)
        {
            return "中";
        }

        return "强";
    }

    // =========================================================================
    // 临时查看明文
    // =========================================================================

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (revealBox.Visibility == Visibility.Visible)
        {
            HideReveal();
            return;
        }

        // 显示明文，5 秒后自动隐藏
        revealBox.Text = _generatedPassword;
        revealBox.Visibility = Visibility.Visible;
        resultBox.Visibility = Visibility.Collapsed;
        revealButton.Content = "隐藏(_H)";
        _a11y.Announce(revealBox, "临时显示密码明文，5 秒后自动隐藏。");

        _revealTimer?.Stop();
        _revealTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _revealTimer.Tick += (_, _) => HideReveal();
        _revealTimer.Start();
        revealBox.Focus();
    }

    private void HideReveal()
    {
        _revealTimer?.Stop();
        _revealTimer = null;
        revealBox.Visibility = Visibility.Collapsed;
        resultBox.Visibility = Visibility.Visible;
        revealButton.Content = "显示(_H)";
    }

    // =========================================================================
    // 键盘与确认
    // =========================================================================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
        // Esc 由“取消”按钮的 IsCancel 处理
    }

    private void OnOk(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (string.IsNullOrEmpty(_generatedPassword))
        {
            Regenerate(announce: false);
        }

        HideReveal();
        DialogResult = true;
        Close();
    }
}
