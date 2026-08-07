using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 话本阅读器对话框。支持小说导入后的自动朗读、章节导航、语速控制、进度记忆。
/// 快捷键: Space 播放/暂停, 左右键 上/下章, Esc 关闭。
/// </summary>
public partial class StoryReaderDialog : Window
{
    private readonly AccessibilityService _a11y = new();
    private readonly StoryEntry _story;
    private readonly StoryCollectionData _storyData;
    private bool _isChapterBoxUpdating;

    /// <summary>阅读结束后保存的最新故事数据（含进度）。</summary>
    public StoryCollectionData? Result { get; private set; }

    public StoryReaderDialog(StoryEntry story, StoryCollectionData storyData)
    {
        InitializeComponent();
        _story = story;
        _storyData = storyData;

        titleBox.Text = story.Title;
        authorBox.Text = story.Author;
        rateSlider.Value = story.ReadingRate;
        rateText.Text = story.ReadingRate.ToString();

        // 填充章节下拉
        _isChapterBoxUpdating = true;
        foreach (var ch in story.Chapters)
        {
            chapterBox.Items.Add($"第{ch.Index}章 {ch.Title}");
        }
        _isChapterBoxUpdating = false;

        if (story.Chapters.Count > 0)
        {
            var idx = Math.Min(story.CurrentChapterIndex, story.Chapters.Count - 1);
            chapterBox.SelectedIndex = idx;
            DisplayChapter(idx);
        }

        // 注册朗读事件
        SpeechService.ReadingProgressChanged += OnReadingProgress;
        SpeechService.ChapterChanged += OnChapterChanged;
        SpeechService.ReadingCompleted += OnReadingCompleted;

        UpdateProgressDisplay();
    }

    /// <summary>显示指定章节内容。</summary>
    private void DisplayChapter(int index)
    {
        if (index < 0 || index >= _story.Chapters.Count) return;

        var chapter = _story.Chapters[index];
        contentBox.Text = chapter.Content;
        chapterInfoText.Text = $"第{chapter.Index}章 / 共{_story.Chapters.Count}章 | {chapter.Content.Length}字";
        contentBox.ScrollToHome();

        _story.CurrentChapterIndex = index;
        _story.CurrentCharPosition = 0;
        _story.LastReadTime = DateTime.Now;
        _story.UpdateProgress();
        UpdateProgressDisplay();
    }

    /// <summary>更新进度显示。</summary>
    private void UpdateProgressDisplay()
    {
        _story.UpdateProgress();
        progressText.Text = $"进度: {_story.ProgressPercent:F1}%";
        progressBar.Value = _story.ProgressPercent;
    }

    // ================================================================
    //  章节导航
    // ================================================================

    private void OnPrevChapter(object sender, RoutedEventArgs e)
    {
        if (chapterBox.SelectedIndex > 0)
        {
            chapterBox.SelectedIndex--;
        }
        else
        {
            _a11y.Announce(prevChapterButton, "已经是第一章了。");
        }
    }

    private void OnNextChapter(object sender, RoutedEventArgs e)
    {
        if (chapterBox.SelectedIndex < chapterBox.Items.Count - 1)
        {
            chapterBox.SelectedIndex++;
        }
        else
        {
            _a11y.Announce(nextChapterButton, "已经是最后一章了。");
        }
    }

    private void OnChapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_isChapterBoxUpdating) return;
        if (chapterBox.SelectedIndex >= 0)
        {
            DisplayChapter(chapterBox.SelectedIndex);
            _a11y.Announce(chapterBox, $"已切换到 {_story.Chapters[chapterBox.SelectedIndex].Title}");
        }
    }

    // ================================================================
    //  朗读控制
    // ================================================================

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (!SpeechService.IsAvailable)
        {
            _a11y.Announce(playButton, "语音朗读不可用，请检查系统是否安装了语音引擎。");
            return;
        }

        if (SpeechService.IsContinuousReading && SpeechService.IsPaused)
        {
            // 恢复
            SpeechService.Resume();
            playButton.Content = "播放中";
            playButton.IsEnabled = false;
            pauseButton.IsEnabled = true;
            stopButton.IsEnabled = true;
            skipButton.IsEnabled = true;
            prevSentenceButton.IsEnabled = true;
            _a11y.Announce(playButton, "已恢复朗读。");
            return;
        }

        // 开始朗读
        var chapters = _story.Chapters.Select(c => c.Content).ToList();
        var titles = _story.Chapters.Select(c => $"第{c.Index}章 {c.Title}").ToList();
        var rate = (int)rateSlider.Value;

        SpeechService.StartContinuousReading(chapters, titles, _story.CurrentChapterIndex, rate);

        playButton.Content = "播放中";
        playButton.IsEnabled = false;
        pauseButton.IsEnabled = true;
        stopButton.IsEnabled = true;
        skipButton.IsEnabled = true;
        prevSentenceButton.IsEnabled = true;
        pauseButton.Content = "暂停";
        statusText.Text = "正在朗读...";
        _a11y.Announce(playButton, "开始朗读。");
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (SpeechService.IsContinuousReading && !SpeechService.IsPaused)
        {
            SpeechService.Pause();
            playButton.Content = "继续(_S)";
            playButton.IsEnabled = true;
            pauseButton.IsEnabled = false;
            statusText.Text = "已暂停";
            _a11y.Announce(pauseButton, "已暂停朗读。");
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        SpeechService.StopAll();
        ResetPlayButtons();
        statusText.Text = "已停止";
        currentSentenceText.Text = "";
        _a11y.Announce(stopButton, "已停止朗读。");
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        SpeechService.SkipNext();
        _a11y.Announce(skipButton, "已跳过当前句子。");
    }

    private void OnReplayChapter(object sender, RoutedEventArgs e)
    {
        SpeechService.StopAll();
        ResetPlayButtons();
        // 重新开始朗读当前章节
        OnPlay(sender, e);
        _a11y.Announce(prevSentenceButton, "重新朗读本章。");
    }

    private void OnRateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var rate = (int)rateSlider.Value;
        rateText.Text = rate.ToString();
        _story.ReadingRate = rate;
        SpeechService.SetRate(rate);
    }

    private void ResetPlayButtons()
    {
        playButton.Content = "播放(_S)";
        playButton.IsEnabled = true;
        pauseButton.IsEnabled = false;
        stopButton.IsEnabled = false;
        skipButton.IsEnabled = false;
        prevSentenceButton.IsEnabled = false;
    }

    // ================================================================
    //  朗读事件回调（在后台线程触发，需 Dispatcher 转到 UI 线程）
    // ================================================================

    private void OnReadingProgress(int chapterIndex, double progress, string sentence)
    {
        Dispatcher.Invoke(() =>
        {
            if (chapterIndex != chapterBox.SelectedIndex)
            {
                _isChapterBoxUpdating = true;
                chapterBox.SelectedIndex = chapterIndex;
                _isChapterBoxUpdating = false;
                DisplayChapter(chapterIndex);
            }

            currentSentenceText.Text = sentence;
            statusText.Text = $"正在朗读: 第{chapterIndex + 1}章 {progress:F0}%";

            // 更新章节内进度
            var chapter = _story.Chapters[chapterIndex];
            _story.CurrentCharPosition = (int)(chapter.Content.Length * progress / 100);
            _story.UpdateProgress();
            progressBar.Value = _story.ProgressPercent;
            progressText.Text = $"进度: {_story.ProgressPercent:F1}%";
        });
    }

    private void OnChapterChanged(int chapterIndex, string chapterTitle)
    {
        Dispatcher.Invoke(() =>
        {
            _a11y.Announce(contentBox, $"正在切换到{chapterTitle}");
        });
    }

    private void OnReadingCompleted()
    {
        Dispatcher.Invoke(() =>
        {
            ResetPlayButtons();
            statusText.Text = "朗读完成";
            currentSentenceText.Text = "";
            _story.CurrentChapterIndex = _story.Chapters.Count - 1;
            _story.CurrentCharPosition = 0;
            _story.UpdateProgress();
            UpdateProgressDisplay();
            _a11y.Announce(playButton, "全书朗读完成。");
        });
    }

    // ================================================================
    //  快捷键
    // ================================================================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Space: 播放/暂停
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            if (SpeechService.IsContinuousReading && !SpeechService.IsPaused)
            {
                OnPause(sender, e);
            }
            else if (SpeechService.IsContinuousReading && SpeechService.IsPaused)
            {
                OnPlay(sender, e);
            }
            else
            {
                OnPlay(sender, e);
            }
            return;
        }

        // 左键: 上一章
        if (e.Key == Key.Left)
        {
            e.Handled = true;
            OnPrevChapter(sender, e);
            return;
        }

        // 右键: 下一章
        if (e.Key == Key.Right)
        {
            e.Handled = true;
            OnNextChapter(sender, e);
            return;
        }

        // Esc: 关闭
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
    }

    // ================================================================
    //  关闭处理
    // ================================================================

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SpeechService.StopAll();
        SpeechService.ReadingProgressChanged -= OnReadingProgress;
        SpeechService.ChapterChanged -= OnChapterChanged;
        SpeechService.ReadingCompleted -= OnReadingCompleted;

        // 保存进度
        _story.ModifiedTime = DateTime.Now;
        _story.UpdateProgress();
        Result = _storyData;
    }
}
