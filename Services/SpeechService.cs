using System.Runtime.InteropServices;

namespace BlindNotepad.Services;

/// <summary>
/// 语音朗读服务。通过 Windows SAPI COM 接口实现文本转语音。
/// 不依赖 NuGet 包，在运行时动态调用 SAPI.SpVoice。
/// </summary>
public static class SpeechService
{
    private static dynamic? _voice;
    private static bool _initialized;

    /// <summary>初始化 SAPI 语音对象。</summary>
    private static void EnsureInitialized()
    {
        if (_initialized) return;
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type is not null)
            {
                _voice = Activator.CreateInstance(type);
                _initialized = true;
            }
        }
        catch
        {
            // SAPI 不可用（非 Windows 或未安装）
        }
    }

    /// <summary>异步朗读文本。</summary>
    /// <param name="text">要朗读的文本</param>
    /// <param name="rate">语速，-5 到 5，0 为正常</param>
    public static void SpeakAsync(string text, int rate = 0)
    {
        Stop();
        EnsureInitialized();
        if (_voice is null) return;

        Task.Run(() =>
        {
            try
            {
                _voice.Rate = rate;
                // SVSFPurgeBeforeSpeak = 0x20 | SVSFlagsAsync = 1
                _voice.Speak(text, 0x21);
            }
            catch
            {
                // 朗读失败静默处理
            }
        });
    }

    /// <summary>停止当前朗读。</summary>
    public static void Stop()
    {
        if (_voice is null) return;
        try
        {
            // SVSFPurgeBeforeSpeak = 0x20
            _voice.Speak("", 0x20);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>SAPI 是否可用。</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _voice is not null;
        }
    }

    /// <summary>获取已安装的语音列表。</summary>
    public static List<string> GetVoices()
    {
        EnsureInitialized();
        if (_voice is null) return new List<string>();
        try
        {
            var voices = _voice.GetVoices();
            var result = new List<string>();
            for (int i = 0; i < voices.Count; i++)
            {
                var voice = voices.Item(i);
                result.Add(voice.GetDescription());
                Marshal.ReleaseComObject(voice);
            }
            Marshal.ReleaseComObject(voices);
            return result;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>设置当前语音。</summary>
    public static void SetVoice(string voiceName)
    {
        EnsureInitialized();
        if (_voice is null) return;
        try
        {
            var voices = _voice.GetVoices($"Name={voiceName}");
            if (voices.Count > 0)
            {
                _voice.Voice = voices.Item(0);
            }
            Marshal.ReleaseComObject(voices);
        }
        catch
        {
            // 忽略
        }
    }
}
