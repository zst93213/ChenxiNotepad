#nullable enable
using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace BlindNotepad.Services
{
    /// <summary>
    /// 剪贴板服务：复制、清空以及延时自动清空（清空后通过回调通知，用于读屏播报）。
    /// </summary>
    public class ClipboardService
    {
        private DispatcherTimer? _autoClearTimer;
        private Action? _onClearedCallback;

        /// <summary>
        /// 复制文本到剪贴板（在 STA 线程执行）。
        /// </summary>
        public void CopyToClipboard(string text)
        {
            RunOnStaThread(() =>
            {
                try
                {
                    Clipboard.SetText(text ?? string.Empty);
                }
                catch
                {
                    // 剪贴板被其它进程占用等情况时忽略，避免抛出异常。
                }
            });
        }

        /// <summary>
        /// 清空剪贴板。
        /// </summary>
        public void ClearClipboard()
        {
            RunOnStaThread(() =>
            {
                try
                {
                    Clipboard.Clear();
                }
                catch
                {
                    // 忽略清空失败。
                }
            });
        }

        /// <summary>
        /// 停止自动清除计时器（不清空剪贴板本身）。用于锁定密码库等场景取消待执行的自动清除。
        /// </summary>
        public void StopAutoClearTimer()
        {
            var timer = _autoClearTimer;
            if (timer != null)
            {
                timer.Stop();
                timer.Tick -= OnAutoClearTimerTick;
            }

            _autoClearTimer = null;
            _onClearedCallback = null;
        }

        /// <summary>
        /// 复制文本后，在指定秒数后自动清空剪贴板，并通过回调通知（用于读屏播报）。
        /// 使用 DispatcherTimer 实现自动清除倒计时。
        /// </summary>
        /// <param name="text">要复制的文本。</param>
        /// <param name="timeoutSeconds">多少秒后自动清空。</param>
        /// <param name="onCleared">清空完成后的回调（在 UI 线程触发）。</param>
        public void CopyWithAutoClear(string text, int timeoutSeconds, Action onCleared)
        {
            CopyToClipboard(text);

            // 停止并清理可能已存在的倒计时，避免重复触发。
            var existing = _autoClearTimer;
            if (existing != null)
            {
                existing.Stop();
                existing.Tick -= OnAutoClearTimerTick;
            }

            _onClearedCallback = onCleared;

            // 绑定到 UI 线程的 Dispatcher，保证 Tick 回调在 UI 线程触发。
            Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _autoClearTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = timeoutSeconds > 0
                    ? TimeSpan.FromSeconds(timeoutSeconds)
                    : TimeSpan.Zero
            };
            _autoClearTimer.Tick += OnAutoClearTimerTick;
            _autoClearTimer.Start();
        }

        private void OnAutoClearTimerTick(object? sender, EventArgs e)
        {
            var timer = _autoClearTimer;
            if (timer != null)
            {
                timer.Stop();
                timer.Tick -= OnAutoClearTimerTick;
            }

            ClearClipboard();

            var callback = _onClearedCallback;
            _onClearedCallback = null;
            callback?.Invoke();
        }

        /// <summary>
        /// 在 STA 线程执行指定操作；若当前线程已是 STA 线程则直接执行。
        /// 剪贴板 API 要求在 STA 线程调用。
        /// </summary>
        private static void RunOnStaThread(Action action)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                action();
                return;
            }

            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (caught != null)
            {
                throw caught;
            }
        }
    }
}
