using System.Runtime.InteropServices;

namespace BlindNotepad.Services;

/// <summary>
/// 语音朗读服务。通过 Windows SAPI COM 接口实现文本转语音。
/// 不依赖 NuGet 包，在运行时动态调用 SAPI.SpVoice。
/// 支持单次朗读和连续朗读模式（用于话本模块）。
/// </summary>
public static class SpeechService
{
    private static dynamic? _voice;
    private static bool _initialized;

    // 连续朗读状态
    private static Thread? _readThread;
    private static CancellationTokenSource? _cts;
    private static readonly object _lock = new();

    /// <summary>朗读状态变化事件。参数: (当前章节索引, 当前章节内进度百分比, 正在朗读的句子)</summary>
    public static event Action<int, double, string>? ReadingProgressChanged;

    /// <summary>章节切换事件。参数: 新章节索引, 章节标题</summary>
    public static event Action<int, string>? ChapterChanged;

    /// <summary>朗读完成事件。</summary>
    public static event Action? ReadingCompleted;

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

    /// <summary>异步朗读文本（单次，用于记事本/日记模块）。</summary>
    /// <param name="text">要朗读的文本</param>
    /// <param name="rate">语速，-5 到 5，0 为正常</param>
    public static void SpeakAsync(string text, int rate = 0)
    {
        StopAll();
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
        StopAll();
    }

    /// <summary>停止所有朗读（单次和连续）。</summary>
    public static void StopAll()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
        }

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

    // ================================================================
    //  连续朗读模式（用于话本模块）
    // ================================================================

    /// <summary>当前是否正在连续朗读。</summary>
    public static bool IsContinuousReading => _cts is not null && !_cts.IsCancellationRequested;

    /// <summary>当前是否暂停。</summary>
    public static bool IsPaused { get; private set; }

    /// <summary>
    /// 开始连续朗读多个章节。
    /// </summary>
    /// <param name="chapters">章节内容列表（每项为章节文本）</param>
    /// <param name="chapterTitles">章节标题列表</param>
    /// <param name="startChapter">起始章节索引</param>
    /// <param name="rate">语速</param>
    public static void StartContinuousReading(
        List<string> chapters,
        List<string> chapterTitles,
        int startChapter,
        int rate)
    {
        StopAll();
        EnsureInitialized();
        if (_voice is null) return;

        IsPaused = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _readThread = new Thread(() =>
        {
            try
            {
                _voice.Rate = rate;

                for (int ch = startChapter; ch < chapters.Count && !token.IsCancellationRequested; ch++)
                {
                    ChapterChanged?.Invoke(ch, ch < chapterTitles.Count ? chapterTitles[ch] : $"第{ch + 1}章");

                    var sentences = StoryService.SplitSentences(chapters[ch]);
                    for (int i = 0; i < sentences.Count; i++)
                    {
                        if (token.IsCancellationRequested) break;

                        // 暂停等待
                        while (IsPaused && !token.IsCancellationRequested)
                        {
                            Thread.Sleep(100);
                        }
                        if (token.IsCancellationRequested) break;

                        var sentence = sentences[i];
                        var progress = sentences.Count > 0 ? (double)i / sentences.Count * 100 : 0;
                        ReadingProgressChanged?.Invoke(ch, progress, sentence);

                        // 同步朗读（阻塞直到这句读完）
                        _voice.Speak(sentence, 0x20); // SVSFPurgeBeforeSpeak
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    ReadingCompleted?.Invoke();
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                // 朗读失败静默处理
            }
            finally
            {
                lock (_lock)
                {
                    _cts = null;
                }
            }
        })
        {
            IsBackground = true,
            Name = "StoryReader"
        };
        _readThread.Start();
    }

    /// <summary>暂停连续朗读。</summary>
    public static void Pause()
    {
        IsPaused = true;
        try { _voice?.Speak("", 0x20); } catch { }
    }

    /// <summary>恢复连续朗读。</summary>
    public static void Resume()
    {
        IsPaused = false;
    }

    /// <summary>跳到下一句（取消当前句，朗读循环自动继续下一句）。</summary>
    public static void SkipNext()
    {
        try { _voice?.Speak("", 0x20); } catch { }
    }

    /// <summary>设置语速。</summary>
    public static void SetRate(int rate)
    {
        try { if (_voice is not null) _voice.Rate = rate; } catch { }
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
