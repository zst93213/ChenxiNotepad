using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 剪贴板监控服务：监听系统剪贴板变化，记录历史条目，
/// 持久化到 clipboard_history.json（明文，最多保留 200 条）。
/// </summary>
public class ClipboardMonitorService : IDisposable
{
    private const int MaxEntries = 200;
    private const string DataFileName = "clipboard_history.json";

    private static readonly string DataFilePath = Path.Combine(StorageService.AppDataDir, DataFileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private HwndSource? _source;
    private IntPtr _handle;
    private bool _isListening;
    private bool _suppressNext;

    /// <summary>剪贴板历史数据（内存缓存）。</summary>
    public List<ClipboardHistoryEntry> History { get; } = new();

    /// <summary>当有新条目添加时触发。</summary>
    public Action<ClipboardHistoryEntry>? OnEntryAdded;

    // Win32 剪贴板监听 API
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// <summary>
    /// 绑定到指定窗口，开始监听剪贴板变化。
    /// </summary>
    public void Initialize(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
        {
            helper.EnsureHandle();
        }

        _handle = helper.Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(HwndHook);

        LoadHistory();
    }

    /// <summary>
    /// 开始监听剪贴板变化。
    /// </summary>
    public void Start()
    {
        if (_isListening || _handle == IntPtr.Zero) return;
        _isListening = AddClipboardFormatListener(_handle);
    }

    /// <summary>
    /// 停止监听剪贴板变化。
    /// </summary>
    public void Stop()
    {
        if (!_isListening || _handle == IntPtr.Zero) return;
        RemoveClipboardFormatListener(_handle);
        _isListening = false;
    }

    /// <summary>
    /// 临时抑制下一次剪贴板事件（防止自身写入触发循环）。
    /// </summary>
    public void SuppressNext()
    {
        _suppressNext = true;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnClipboardChanged();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        if (_suppressNext)
        {
            _suppressNext = false;
            return;
        }

        try
        {
            // 仅记录文本
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;

            // 如果与最新一条内容相同，跳过
            if (History.Count > 0 && History[0].Content == text) return;

            var entry = new ClipboardHistoryEntry
            {
                Content = text,
                Timestamp = DateTime.Now,
            };

            History.Insert(0, entry);

            // 超出上限时移除最旧的（非置顶项）
            while (History.Count > MaxEntries)
            {
                var last = History.LastOrDefault(e => !e.IsPinned);
                if (last != null) History.Remove(last);
                else break;
            }

            SaveHistory();
            OnEntryAdded?.Invoke(entry);
        }
        catch
        {
            // 剪贴板访问失败时静默处理
        }
    }

    /// <summary>
    /// 手动添加一条记录（用于编辑后更新）。
    /// </summary>
    public void UpdateEntry(ClipboardHistoryEntry entry)
    {
        var index = History.FindIndex(e => e.Id == entry.Id);
        if (index >= 0)
        {
            History[index] = entry;
            SaveHistory();
        }
    }

    /// <summary>
    /// 删除指定条目。
    /// </summary>
    public void RemoveEntry(string id)
    {
        var entry = History.FirstOrDefault(e => e.Id == id);
        if (entry != null)
        {
            History.Remove(entry);
            SaveHistory();
        }
    }

    /// <summary>
    /// 清空所有非置顶的历史记录。
    /// </summary>
    public void ClearAll()
    {
        History.RemoveAll(e => !e.IsPinned);
        SaveHistory();
    }

    /// <summary>
    /// 将指定条目内容复制到剪贴板（抑制触发）。
    /// </summary>
    public void CopyToClipboard(ClipboardHistoryEntry entry)
    {
        SuppressNext();
        try
        {
            Clipboard.SetText(entry.Content);
        }
        catch
        {
            // 忽略剪贴板访问失败
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(DataFilePath))
            {
                History.Clear();
                return;
            }

            var json = File.ReadAllText(DataFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                History.Clear();
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<ClipboardHistoryEntry>>(json, JsonOptions);
            History.Clear();
            if (loaded != null)
            {
                History.AddRange(loaded);
            }
        }
        catch
        {
            History.Clear();
        }
    }

    private void SaveHistory()
    {
        try
        {
            var dir = Path.GetDirectoryName(DataFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(History, JsonOptions);
            File.WriteAllText(DataFilePath, json);
        }
        catch
        {
            // 保存失败时静默处理
        }
    }

    public void Dispose()
    {
        Stop();
        _source?.RemoveHook(HwndHook);
        _source = null;
    }
}
