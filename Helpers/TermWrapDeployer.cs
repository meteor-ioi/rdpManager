using System;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using Microsoft.Win32;

namespace rdpManager.Helpers
{
    public static class TermWrapDeployer
    {
        private const string RDP_WRAPPER_DIR = @"C:\Program Files\RDP Wrapper";
        private const string TERM_SERVICE_REG_PATH = @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";
        private const string DEFAULT_SERVICE_DLL = @"%SystemRoot%\System32\termsrv.dll";
        private const string PATCHED_SERVICE_DLL = RDP_WRAPPER_DIR + @"\TermWrap.dll";

        /// <summary>
        /// 检查并发 RDP 是否已激活（即服务 DLL 是否已替换为 TermWrap.dll）
        /// </summary>
        public static bool IsMultiSessionActive()
        {
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(TERM_SERVICE_REG_PATH))
                {
                    if (key != null)
                    {
                        string? serviceDll = key.GetValue("ServiceDll") as string;
                        return string.Equals(serviceDll, PATCHED_SERVICE_DLL, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("检查并发远程桌面激活状态失败", ex);
            }
            return false;
        }

        /// <summary>
        /// 一键部署/修复 TermWrap 补丁
        /// </summary>
        public static bool DeployPatch(out string errorMessage)
        {
            errorMessage = string.Empty;
            Logger.LogInfo("开始部署 TermWrap 补丁...");
            try
            {
                // 1. 创建 RDP Wrapper 文件夹
                if (!Directory.Exists(RDP_WRAPPER_DIR))
                {
                    Directory.CreateDirectory(RDP_WRAPPER_DIR);
                }

                // 2. 释放内嵌的 DLL 资源 (TermWrap.dll, UmWrap.dll, Zydis.dll)
                ExtractResource("TermWrap.dll", Path.Combine(RDP_WRAPPER_DIR, "TermWrap.dll"));
                ExtractResource("UmWrap.dll", Path.Combine(RDP_WRAPPER_DIR, "UmWrap.dll"));
                ExtractResource("Zydis.dll", Path.Combine(RDP_WRAPPER_DIR, "Zydis.dll"));

                // 3. 停止远程桌面服务
                ControlService("TermService", stop: true);

                // 4. 修改注册表劫持 ServiceDll
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(TERM_SERVICE_REG_PATH, true))
                {
                    if (key == null)
                    {
                        errorMessage = "未能打开 TermService 注册表项。";
                        Logger.LogWarning("部署失败: 未能打开 TermService 注册表项。");
                        return false;
                    }
                    key.SetValue("ServiceDll", PATCHED_SERVICE_DLL, RegistryValueKind.ExpandString);
                }

                // 5. 注入 RDP 外设重定向优化配置
                ApplyDeviceRedirectionPolicies();

                // 6. 重启远程桌面服务
                ControlService("TermService", stop: false);

                Logger.LogInfo("TermWrap 补丁部署成功。");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Logger.LogError("部署 TermWrap 补丁发生致命异常，正在自动回滚注册表...", ex);
                try
                {
                    using (RegistryKey? rollbackKey = Registry.LocalMachine.OpenSubKey(TERM_SERVICE_REG_PATH, true))
                    {
                        rollbackKey?.SetValue("ServiceDll", DEFAULT_SERVICE_DLL, RegistryValueKind.ExpandString);
                    }
                    Logger.LogInfo("注册表已成功回滚至原厂 termsrv.dll。");
                }
                catch (Exception rollbackEx)
                {
                    Logger.LogError("回滚注册表失败！系统重启后可能无法正常进入桌面。", rollbackEx);
                }
                return false;
            }
        }

        /// <summary>
        /// 一键卸载补丁，恢复系统原始设置
        /// </summary>
        public static bool UninstallPatch(out string errorMessage)
        {
            errorMessage = string.Empty;
            Logger.LogInfo("开始卸载 TermWrap 补丁...");
            try
            {
                // 1. 停止远程桌面服务
                ControlService("TermService", stop: true);

                // 2. 恢复注册表 ServiceDll 为系统默认的 termsrv.dll
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(TERM_SERVICE_REG_PATH, true))
                {
                    key?.SetValue("ServiceDll", DEFAULT_SERVICE_DLL, RegistryValueKind.ExpandString);
                }

                // 3. 重新启动服务
                ControlService("TermService", stop: false);

                // 4. 尝试删除临时文件与目录（由于 DLL 可能会被进程锁住，若失败提示重启是正常的）
                try
                {
                    if (Directory.Exists(RDP_WRAPPER_DIR))
                    {
                        Directory.Delete(RDP_WRAPPER_DIR, true);
                    }
                }
                catch (Exception deleteEx)
                {
                    errorMessage = "补丁已卸载，但部分 DLL 仍被系统锁定，重启电脑后 RDP Wrapper 文件夹将被完全删除。";
                    Logger.LogWarning($"卸载补丁后清理文件夹受阻 (预期行为): {deleteEx.Message}");
                }

                Logger.LogInfo("TermWrap 补丁卸载操作执行完毕。");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Logger.LogError("卸载 TermWrap 补丁发生致命异常", ex);
                return false;
            }
        }

        /// <summary>
        /// 控制服务的停止与启动
        /// </summary>
        private static void ControlService(string serviceName, bool stop)
        {
            using (ServiceController sc = new ServiceController(serviceName))
            {
                if (stop)
                {
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                }
                else
                {
                    if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    }
                }
            }
        }

        /// <summary>
        /// 自动将程序集中嵌入的资源文件输出到本地路径
        /// </summary>
        private static void ExtractResource(string resourceName, string outputPath)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            // 内嵌资源的命名空间路径通常为: 命名空间.Resources.文件名
            string resourcePath = $"{assembly.GetName().Name}.Resources.{resourceName}";

            using (Stream? stream = assembly.GetManifestResourceStream(resourcePath))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException($"无法在资源中找到: {resourceName}");
                }

                using (FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }
            }
        }

        /// <summary>
        /// 配置组策略注册表，启用设备、USB、摄像头等远程重定向
        /// </summary>
        private static void ApplyDeviceRedirectionPolicies()
        {
            try
            {
                // 开启远程访问 Plug and Play 接口 (USB重定向依赖)
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Consent", true))
                {
                    key.SetValue("AllowRemoteAccessToPSS", 1, RegistryValueKind.DWord);
                }

                // 启用剪贴板和外设重定向（确保不被组策略禁用）
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", true))
                {
                    key.SetValue("fDisablePNPRedir", 0, RegistryValueKind.DWord);
                    key.SetValue("fDisableClipRedir", 0, RegistryValueKind.DWord);
                    key.SetValue("fDisableCdmAllowed", 0, RegistryValueKind.DWord); // 驱动器重定向
                }
            }
            catch
            {
                // 容错处理
            }
        }
    }
}
