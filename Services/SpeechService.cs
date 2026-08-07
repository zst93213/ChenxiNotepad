using System.Runtime.InteropServices;
using BlindNotepad.Models;

namespace BlindNotepad.Services;

/// <summary>
/// 语音朗读服务。通过 Windows SAPI COM 接口实现文本转语音。
/// 不依赖 NuGet 包，在运行时动态调用 SAPI.SpVoice。
/// 支持单次朗读、连续朗读模式（纯文本）和多角色配音模式（画本）。
/// </summary>
public static class SpeechService
{
    private static dynamic? _voice;
    private static dynamic? _maleVoice;
    private static dynamic? _femaleVoice;
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

    /// <summary>角色切换事件。参数: 角色名, 行类型描述</summary>
    public static event Action<string, string>? SpeakerChanged;

    /// <summary>初始化 SAPI 语音对象，尝试获取男女声。</summary>
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

                // 尝试获取男女声
                TryLoadGenderVoices();
            }
        }
        catch
        {
            // SAPI 不可用（非 Windows 或未安装）
        }
    }

    /// <summary>尝试加载男声和女声。</summary>
    private static void TryLoadGenderVoices()
    {
        if (_voice is null) return;
        try
        {
            var voices = _voice.GetVoices();
            var maleFound = false;
            var femaleFound = false;

            for (int i = 0; i < voices.Count; i++)
            {
                var voice = voices.Item(i);
                var desc = voice.GetDescription();

                // 中文女声关键词
                if (!femaleFound && (desc.Contains("Huihui") || desc.Contains("Yaoyao") ||
                    desc.Contains("Female") || desc.Contains("女") || desc.Contains("Yaoyao")))
                {
                    _femaleVoice = voice;
                    femaleFound = true;
                    continue;
                }

                // 中文男声关键词
                if (!maleFound && (desc.Contains("Kangkang") || desc.Contains("Huihui") == false &&
                    (desc.Contains("Male") || desc.Contains("男"))))
                {
                    _maleVoice = voice;
                    maleFound = true;
                    continue;
                }

                Marshal.ReleaseComObject(voice);
            }
            Marshal.ReleaseComObject(voices);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>异步朗读文本（单次，用于记事本/日记模块）。</summary>
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
                _voice.Speak(text, 0x21);
            }
            catch
            {
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
            _voice.Speak("", 0x20);
        }
        catch
        {
        }
    }

    // ================================================================
    //  连续朗读模式（纯文本小说）
    // ================================================================

    /// <summary>当前是否正在连续朗读。</summary>
    public static bool IsContinuousReading => _cts is not null && !_cts.IsCancellationRequested;

    /// <summary>当前是否暂停。</summary>
    public static bool IsPaused { get; private set; }

    /// <summary>
    /// 开始连续朗读多个章节（纯文本模式）。
    /// </summary>
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

                        while (IsPaused && !token.IsCancellationRequested)
                        {
                            Thread.Sleep(100);
                        }
                        if (token.IsCancellationRequested) break;

                        var sentence = sentences[i];
                        var progress = sentences.Count > 0 ? (double)i / sentences.Count * 100 : 0;
                        ReadingProgressChanged?.Invoke(ch, progress, sentence);

                        _voice.Speak(sentence, 0x20);
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

    // ================================================================
    //  多角色配音模式（画本/广播剧）
    // ================================================================

    /// <summary>
    /// 开始多角色配音朗读（画本格式）。
    /// 根据角色性别切换男声/女声，朗读台词前先播报角色名。
    /// </summary>
    /// <param name="chapters">章节列表（含台词行）</param>
    /// <param name="chapterTitles">章节标题列表</param>
    /// <param name="characters">角色列表（用于查找性别）</param>
    /// <param name="startChapter">起始章节索引</param>
    /// <param name="rate">语速</param>
    /// <param name="announceSpeaker">是否在台词前播报角色名</param>
    public static void StartMultiVoiceReading(
        List<List<DialogueLine>> chapters,
        List<string> chapterTitles,
        List<StoryCharacter> characters,
        int startChapter,
        int rate,
        bool announceSpeaker)
    {
        StopAll();
        EnsureInitialized();
        if (_voice is null) return;

        IsPaused = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // 构建角色名到性别的映射
        var charGenderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in characters)
        {
            charGenderMap[c.Name] = c.Gender;
        }

        _readThread = new Thread(() =>
        {
            try
            {
                _voice.Rate = rate;

                for (int ch = startChapter; ch < chapters.Count && !token.IsCancellationRequested; ch++)
                {
                    ChapterChanged?.Invoke(ch, ch < chapterTitles.Count ? chapterTitles[ch] : $"第{ch + 1}集");

                    var lines = chapters[ch];
                    int totalLines = lines.Count;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (token.IsCancellationRequested) break;

                        while (IsPaused && !token.IsCancellationRequested)
                        {
                            Thread.Sleep(100);
                        }
                        if (token.IsCancellationRequested) break;

                        var line = lines[i];
                        var progress = totalLines > 0 ? (double)i / totalLines * 100 : 0;

                        if (line.Type == LineType.Narration)
                        {
                            // 旁白用默认声音
                            SpeakerChanged?.Invoke("旁白", "旁白");
                            ReadingProgressChanged?.Invoke(ch, progress, line.Text);

                            var sentences = StoryService.SplitSentences(line.Text);
                            foreach (var s in sentences)
                            {
                                if (token.IsCancellationRequested) break;
                                while (IsPaused && !token.IsCancellationRequested) Thread.Sleep(100);
                                if (token.IsCancellationRequested) break;
                                SetVoiceForGender("", rate);
                                _voice.Speak(s, 0x20);
                            }
                        }
                        else if (line.Type == LineType.SoundEffect)
                        {
                            // 音效行跳过朗读（仅显示）
                            SpeakerChanged?.Invoke("后期", "音效");
                            ReadingProgressChanged?.Invoke(ch, progress, $"〔音效〕{line.Text}");
                        }
                        else if (line.Type == LineType.Dialogue || line.Type == LineType.InnerThought)
                        {
                            // 台词：根据角色性别切换声音
                            var gender = charGenderMap.TryGetValue(line.CharacterName, out var g) ? g : "";
                            var typeDesc = line.IsInnerThought ? "内心独白" : "台词";
                            SpeakerChanged?.Invoke(line.CharacterName, typeDesc);

                            var displayText = line.IsInnerThought
                                ? $"（{line.CharacterName}心想）{line.Text}"
                                : $"{line.CharacterName}：{line.Text}";
                            ReadingProgressChanged?.Invoke(ch, progress, displayText);

                            // 播报角色名
                            if (announceSpeaker && !string.IsNullOrEmpty(line.CharacterName))
                            {
                                SetVoiceForGender(gender, rate);
                                var speakerPrefix = line.IsInnerThought
                                    ? $"{line.CharacterName}心想。"
                                    : $"{line.CharacterName}说。";
                                _voice.Speak(speakerPrefix, 0x20);
                            }

                            // 朗读台词内容
                            SetVoiceForGender(gender, rate);
                            var dialogueSentences = StoryService.SplitSentences(line.Text);
                            foreach (var s in dialogueSentences)
                            {
                                if (token.IsCancellationRequested) break;
                                while (IsPaused && !token.IsCancellationRequested) Thread.Sleep(100);
                                if (token.IsCancellationRequested) break;
                                _voice.Speak(s, 0x20);
                            }
                        }
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
            Name = "AudioDramaReader"
        };
        _readThread.Start();
    }

    /// <summary>根据性别设置语音和语速。</summary>
    private static void SetVoiceForGender(string gender, int rate)
    {
        try
        {
            _voice!.Rate = rate;

            if (gender == "男" && _maleVoice is not null)
            {
                _voice.Voice = _maleVoice;
            }
            else if (gender == "女" && _femaleVoice is not null)
            {
                _voice.Voice = _femaleVoice;
            }
            // 如果没有对应性别声音或性别为"无"，保持默认声音
        }
        catch
        {
        }
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

    /// <summary>跳到下一句。</summary>
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

    /// <summary>是否有男声可用。</summary>
    public static bool HasMaleVoice
    {
        get
        {
            EnsureInitialized();
            return _maleVoice is not null;
        }
    }

    /// <summary>是否有女声可用。</summary>
    public static bool HasFemaleVoice
    {
        get
        {
            EnsureInitialized();
            return _femaleVoice is not null;
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
        }
    }
}
