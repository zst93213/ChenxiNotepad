#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace BlindNotepad.Services
{
    /// <summary>
    /// 无障碍服务：检测读屏软件、通过争渡读屏 API 或 UIA LiveRegion 朗读/通知。
    /// </summary>
    public class AccessibilityService
    {
        private const uint SPI_GETSCREENREADER = 0x0046;

        /// <summary>
        /// 通过 SystemParametersInfo(SPI_GETSCREENREADER) 检测当前是否有读屏软件运行。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

        /// <summary>
        /// 争渡读屏 API。实际 C 声明为：void Speak(wchar_t* text, int bInterrupt)
        /// 此处 C# 方法命名为 SpeakText（按需求约定），通过 EntryPoint 映射到导出函数 Speak。
        /// 文本参数采用 string 方式（CharSet.Unicode -> wchar_t*），interrupt 采用 bool
        /// 并显式声明为 UnmanagedType.Bool（对应 4 字节 int）。
        /// 该 DLL 不存在时调用会抛出 DllNotFoundException，由调用方捕获并回退。
        /// </summary>
        private const string ZDSRDllName = "ZDSRAPI_x64.dll";

        [DllImport(ZDSRDllName, EntryPoint = "Speak", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        private static extern void SpeakText(string text, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

        // 争渡可用性缓存：0 = 未知，1 = 可用，2 = 不可用
        private static int _zdsrState;

        /// <summary>
        /// 检测当前是否有读屏软件运行。
        /// </summary>
        public bool IsScreenReaderRunning()
        {
            bool result = false;
            try
            {
                SystemParametersInfo(SPI_GETSCREENREADER, 0, ref result, 0);
            }
            catch
            {
                // P/Invoke 失败时视为无读屏软件。
            }

            return result;
        }

        /// <summary>
        /// 朗读文本：优先使用争渡读屏的 ZDSRAPI_x64.dll；若争渡不可用则回退到 UIA LiveRegion 通知。
        /// </summary>
        public void Speak(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (TrySpeakViaZDSR(text))
            {
                return;
            }

            AnnounceLiveRegion(text);
        }

        /// <summary>
        /// 通过 UIA LiveRegion 通知读屏：设置元素名称并触发 LiveRegionChanged 事件。
        /// </summary>
        public void Announce(FrameworkElement element, string message)
        {
            if (element == null || string.IsNullOrEmpty(message))
            {
                return;
            }

            OnUiThread(() => AnnounceViaLiveRegion(element, message));
        }

        /// <summary>
        /// 尝试通过争渡读屏朗读。DLL 或函数不可用时返回 false 以便调用方回退。
        /// </summary>
        private static bool TrySpeakViaZDSR(string text)
        {
            if (_zdsrState == 2)
            {
                return false;
            }

            try
            {
                // interrupt = true：打断当前正在朗读的内容
                SpeakText(text, true);
                _zdsrState = 1;
                return true;
            }
            catch (DllNotFoundException)
            {
                _zdsrState = 2;
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                _zdsrState = 2;
                return false;
            }
            catch
            {
                // 其它异常（如争渡内部错误）也回退到 LiveRegion。
                return false;
            }
        }

        /// <summary>
        /// 选择当前焦点元素（或主窗口）作为 LiveRegion 目标进行通知。
        /// </summary>
        private static void AnnounceLiveRegion(string text)
        {
            OnUiThread(() =>
            {
                FrameworkElement? target = null;
                try
                {
                    Window? window = Application.Current?.MainWindow;
                    if (window != null)
                    {
                        target = FocusManager.GetFocusedElement(window) as FrameworkElement ?? window;
                    }
                }
                catch
                {
                    // 忽略获取焦点元素的异常。
                }

                if (target != null)
                {
                    AnnounceViaLiveRegion(target, text);
                }
            });
        }

        /// <summary>
        /// 在指定元素上设置名称并触发 LiveRegionChanged 事件（必须在 UI 线程调用）。
        /// </summary>
        private static void AnnounceViaLiveRegion(FrameworkElement element, string message)
        {
            try
            {
                AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
                AutomationProperties.SetName(element, message);

                AutomationPeer? peer = UIElementAutomationPeer.FromElement(element);
                if (peer == null)
                {
                    peer = UIElementAutomationPeer.CreatePeerForElement(element);
                }

                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
            catch
            {
                // LiveRegion 通知失败时静默处理，避免影响主流程。
            }
        }

        /// <summary>
        /// 若当前不在 UI 线程，则将操作投递到 UI 线程的 Dispatcher 执行。
        /// </summary>
        private static void OnUiThread(Action action)
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                action();
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
            }
        }
    }
}
