using System.Runtime.InteropServices;

namespace BlindNotepad.Services;

/// <summary>
/// 语音输入服务。通过模拟 Windows 内置语音输入快捷键（Win+H）启动听写。
/// 支持 Windows 10/11 内置语音识别。
/// </summary>
public static class VoiceInputService
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_LWIN = 0x5B; // Left Windows key
    private const byte VK_H = 0x48;    // H key
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// 启动 Windows 内置语音听写（模拟 Win+H）。
    /// 文字会自动输入到当前焦点所在的编辑框。
    /// </summary>
    public static void StartDictation()
    {
        // 模拟 Win+H 按键
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);    // Win down
        keybd_event(VK_H, 0, 0, UIntPtr.Zero);        // H down
        keybd_event(VK_H, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);      // H up
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);   // Win up
    }

    /// <summary>检查系统是否支持语音听写。</summary>
    public static bool IsSupported => Environment.OSVersion.Platform == PlatformID.Win32NT;
}
