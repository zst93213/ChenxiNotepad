#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BlindNotepad
{
    /// <summary>
    /// 应用程序入口：注册未捕获异常处理并初始化存储目录。
    /// </summary>
    public partial class App : Application
    {
        private const string AppDataFolderName = "SuixinJi";

        /// <summary>错误日志文件路径（与密码库同目录）。</summary>
        private static readonly string ErrorLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataFolderName, "error.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            // 在主窗口创建前注册异常处理与初始化存储目录。
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            InitializeStorageDirectory();

            base.OnStartup(e);
        }

        private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ShowError("发生未处理的异常", e.Exception);
            e.Handled = true;
        }

        private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            ShowError("发生严重未处理异常", e.ExceptionObject as Exception);
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            ShowError("后台任务发生未观察的异常", e.Exception);
            e.SetObserved();
        }

        private static void ShowError(string title, Exception? exception)
        {
            // 将完整异常信息写入日志，便于回溯排查。
            try
            {
                var dir = Path.GetDirectoryName(ErrorLogPath);
                if (dir is not null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var logEntry = exception != null
                    ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}\r\n{exception}\r\n{new string('-', 60)}\r\n"
                    : $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}\r\n发生未知异常。\r\n{new string('-', 60)}\r\n";

                File.AppendAllText(ErrorLogPath, logEntry);
            }
            catch
            {
                // 日志写入失败不阻止弹框。
            }

            // 弹框提示用户，并告知日志位置。
            string message = exception != null
                ? $"{exception.Message}\r\n\r\n详细错误已记录到：\r\n{ErrorLogPath}"
                : "发生未知异常。";

            try
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 弹框本身失败时忽略，避免在异常处理流程中再次抛出。
            }
        }

        /// <summary>
        /// 初始化本地应用数据存储目录。
        /// </summary>
        private static void InitializeStorageDirectory()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppDataFolderName);
                Directory.CreateDirectory(path);
            }
            catch
            {
                // 创建失败时忽略，不阻止应用启动。
            }
        }
    }
}
