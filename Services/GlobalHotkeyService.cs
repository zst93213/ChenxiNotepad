using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 全局热键服务：通过 Win32 RegisterHotKey API 注册系统级热键，
/// 在应用窗口失焦时也能响应。热键消息通过 HwndSource 消息泵接收。
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    /// <summary>热键回调委托。</summary>
    public Action<int>? OnHotkeyPressed;

    private HwndSource? _source;
    private readonly Dictionary<int, string> _registeredKeys = new();
    private int _nextId = 9000;

    // Win32 常量
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// 绑定到指定窗口，开始监听热键消息。
    /// </summary>
    public void Initialize(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
        {
            helper.EnsureHandle();
        }

        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(HwndHook);
    }

    /// <summary>
    /// 注册一个全局热键。
    /// </summary>
    /// <param name="key">WPF Key 枚举</param>
    /// <param name="ctrl">是否包含 Ctrl</param>
    /// <param name="shift">是否包含 Shift</param>
    /// <param name="alt">是否包含 Alt</param>
    /// <param name="win">是否包含 Win 键</param>
    /// <returns>热键 ID，用于注销时引用；注册失败返回 -1。</returns>
    public int Register(System.Windows.Input.Key key, bool ctrl, bool shift, bool alt = false, bool win = false)
    {
        if (_source == null) return -1;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        uint modifiers = MOD_NOREPEAT;
        if (ctrl) modifiers |= MOD_CONTROL;
        if (shift) modifiers |= MOD_SHIFT;
        if (alt) modifiers |= MOD_ALT;
        if (win) modifiers |= MOD_WIN;

        int id = _nextId++;
        if (RegisterHotKey(_source.Handle, id, modifiers, (uint)vk))
        {
            _registeredKeys[id] = $"{(ctrl ? "Ctrl+" : "")}{(shift ? "Shift+" : "")}{(alt ? "Alt+" : "")}{key}";
            return id;
        }
        return -1;
    }

    /// <summary>
    /// 注销指定热键。
    /// </summary>
    public void Unregister(int id)
    {
        if (_source == null || !_registeredKeys.ContainsKey(id)) return;
        UnregisterHotKey(_source.Handle, id);
        _registeredKeys.Remove(id);
    }

    /// <summary>
    /// 注销所有热键。
    /// </summary>
    public void UnregisterAll()
    {
        if (_source == null) return;
        foreach (var id in _registeredKeys.Keys.ToList())
        {
            UnregisterHotKey(_source.Handle, id);
        }
        _registeredKeys.Clear();
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registeredKeys.ContainsKey(id))
            {
                OnHotkeyPressed?.Invoke(id);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(HwndHook);
        _source = null;
    }
}
