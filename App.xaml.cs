using System;
using System.IO;
using System.Windows;
using System.Threading.Tasks;
using rdpManager.Helpers;

namespace rdpManager
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"注册 CodePagesEncodingProvider 失败: {ex.Message}");
            }

            ThemeManager.Initialize();

            base.OnStartup(e);

            // 初始化未捕获异常处理
            SetupExceptionHandling();

            Logger.LogInfo("================ 应用程序启动 ================");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.LogInfo($"================ 应用程序退出 (退出码: {e.ApplicationExitCode}) ================");
            base.OnExit(e);
        }

        private void SetupExceptionHandling()
        {
            // 1. 捕获 UI 线程未处理异常
            this.DispatcherUnhandledException += (s, args) =>
            {
                Logger.LogError("UI 线程发生未处理异常", args.Exception);
                ShowCrashReport(args.Exception, "UI 线程致命错误");
                args.Handled = true;
                Shutdown(-1);
            };

            // 2. 捕获非 UI 线程未处理异常
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Exception? ex = args.ExceptionObject as Exception;
                Logger.LogError("应用程序域发生未处理异常", ex);
                if (ex != null)
                {
                    ShowCrashReport(ex, "后台线程致命错误");
                }
                Shutdown(-1);
            };

            // 3. 捕获未观察到的 Task 任务异常
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Logger.LogError("异步任务中发生未捕获异常", args.Exception);
                args.SetObserved();
            };
        }

        private void ShowCrashReport(Exception ex, string title)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "rdpManager.log");
                string userMessage = $"抱歉，程序遇到了致命错误并即将退出。\n\n" +
                                     $"错误原因: {ex.Message}\n\n" +
                                     $"详细日志已保存至:\n{logPath}\n\n" +
                                     $"请将日志提供给开发团队以协助排查。";

                MessageBox.Show(userMessage, title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 容错处理
            }
        }
    }
}
