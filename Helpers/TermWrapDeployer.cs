using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
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
        /// 检查音频保活 EndpWrap 是否已激活（即 AudioEnumeratorDll 是否已替换为 EndpWrap.dll）
        /// </summary>
        public static bool IsEndpWrapActive()
        {
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp"))
                {
                    if (key != null)
                    {
                        string? audioDll = key.GetValue("AudioEnumeratorDll") as string;
                        return string.Equals(audioDll, "EndpWrap.dll", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("检查音频保活激活状态失败", ex);
            }
            return false;
        }

        /// <summary>
        /// 检查 TermService 服务是否正在运行
        /// </summary>
        public static bool IsTermServiceRunning()
        {
            try
            {
                using (ServiceController sc = new ServiceController("TermService"))
                {
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("检查 TermService 运行状态失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 一键部署/修复 TermWrap 补丁
        /// </summary>
        public static bool DeployPatch(out string errorMessage)
        {
            errorMessage = string.Empty;
            Logger.LogInfo("开始部署 TermWrap 补丁及音频保活补丁...");
            try
            {
                // 1. 创建 RDP Wrapper 文件夹
                if (!Directory.Exists(RDP_WRAPPER_DIR))
                {
                    Directory.CreateDirectory(RDP_WRAPPER_DIR);
                }

                // 2. 强制注销所有活跃 RDP 会话，再停止服务（确保文件在写入时未被占用）
                KillAllRdpSessions();
                ControlService("TermService", stop: true);

                // 3. 释放内嵌的 DLL 资源 (TermWrap.dll, UmWrap.dll, Zydis.dll)
                ExtractResource("TermWrap.dll", Path.Combine(RDP_WRAPPER_DIR, "TermWrap.dll"));
                ExtractResource("UmWrap.dll", Path.Combine(RDP_WRAPPER_DIR, "UmWrap.dll"));
                ExtractResource("Zydis.dll", Path.Combine(RDP_WRAPPER_DIR, "Zydis.dll"));

                // 释放内嵌的 EndpWrap.dll 到 System32 目录
                string system32Path = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string endpSystem32Path = Path.Combine(system32Path, "EndpWrap.dll");
                try
                {
                    ExtractResource("EndpWrap.dll", endpSystem32Path);
                    Logger.LogInfo("EndpWrap.dll 成功释放至 System32 目录。");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"释放 EndpWrap.dll 到 System32 失败: {ex.Message}。");
                }

                // 4. 额外部署 Zydis.dll 到 System32 目录，这是确保 svchost.exe 能够成功加载 TermWrap.dll 的关键
                string zydisSystem32Path = Path.Combine(system32Path, "Zydis.dll");
                try
                {
                    ExtractResource("Zydis.dll", zydisSystem32Path);
                    Logger.LogInfo("Zydis.dll 成功释放至 System32 目录。");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"释放 Zydis.dll 到 System32 失败: {ex.Message}。如果该文件已存在，可能不会影响正常加载。");
                }

                // 5. 修改注册表劫持 ServiceDll
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

                // 修改注册表劫持 AudioEnumeratorDll
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", true))
                {
                    if (key != null)
                    {
                        if (key.GetValue("LocalRDP_Backup") == null)
                        {
                            var originalDll = key.GetValue("AudioEnumeratorDll")?.ToString() ?? "rdpendp.dll";
                            key.SetValue("LocalRDP_Backup", originalDll, RegistryValueKind.String);
                        }
                        key.SetValue("AudioEnumeratorDll", "EndpWrap.dll", RegistryValueKind.String);
                        Logger.LogInfo("RDP-Tcp AudioEnumeratorDll 注册表项已劫持为 EndpWrap.dll。");
                    }
                }

                // 6. 恢复 TermService 服务类型为共享进程 (0x20)。之前尝试使用 0x10 独立进程会破坏 Windows 系统的 RPC/COM 绑定安全上下文，导致 516 连接拒绝。
                //    同时设置 SvcHostSplitDisable=1，防止 Windows 10 自动将共享进程服务拆分为独立进程（此行为会导致运行时 Type 回退为 0x10）。
                using (RegistryKey? svcKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TermService", true))
                {
                    if (svcKey != null)
                    {
                        svcKey.SetValue("Type", 0x20, RegistryValueKind.DWord);
                        svcKey.SetValue("SvcHostSplitDisable", 1, RegistryValueKind.DWord);
                        Logger.LogInfo("已确保 TermService 服务运行类型为默认共享进程 (0x20)，并禁止系统自动拆分。");
                    }
                }

                // 7. 注入 RDP 外设重定向优化配置
                ApplyDeviceRedirectionPolicies();

                // 8. 重启远程桌面服务
                ControlService("TermService", stop: false);

                Logger.LogInfo("TermWrap 及音频保活补丁部署成功。");
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Logger.LogError("部署 TermWrap 补丁发生致命异常，正在自动回滚注册表...", ex);
                try
                {
                    // 回滚 ServiceDll 注册表
                    using (RegistryKey? rollbackKey = Registry.LocalMachine.OpenSubKey(TERM_SERVICE_REG_PATH, true))
                    {
                        rollbackKey?.SetValue("ServiceDll", DEFAULT_SERVICE_DLL, RegistryValueKind.ExpandString);
                    }
                    // 回滚服务 Type 注册表为共享进程 (0x20)
                    using (RegistryKey? svcRollbackKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TermService", true))
                    {
                        svcRollbackKey?.SetValue("Type", 0x20, RegistryValueKind.DWord);
                    }
                    // 回滚 AudioEnumeratorDll 注册表
                    using (RegistryKey? rollbackAudioKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", true))
                    {
                        if (rollbackAudioKey != null)
                        {
                            var backupDll = rollbackAudioKey.GetValue("LocalRDP_Backup")?.ToString();
                            if (backupDll != null)
                            {
                                rollbackAudioKey.SetValue("AudioEnumeratorDll", backupDll, RegistryValueKind.String);
                                rollbackAudioKey.DeleteValue("LocalRDP_Backup", false);
                            }
                            else
                            {
                                rollbackAudioKey.SetValue("AudioEnumeratorDll", "rdpendp.dll", RegistryValueKind.String);
                            }
                        }
                    }
                    Logger.LogInfo("注册表及服务类型已成功回滚至原厂配置。");
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
            Logger.LogInfo("开始卸载 TermWrap 补丁及音频保活补丁...");
            try
            {
                // 1. 强制注销所有活跃 RDP 会话，再停止服务
                KillAllRdpSessions();
                ControlService("TermService", stop: true);

                // 2. 恢复注册表 ServiceDll 为系统默认的 termsrv.dll
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(TERM_SERVICE_REG_PATH, true))
                {
                    key?.SetValue("ServiceDll", DEFAULT_SERVICE_DLL, RegistryValueKind.ExpandString);
                }

                // 恢复注册表 AudioEnumeratorDll 为备份或默认的 rdpendp.dll
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", true))
                {
                    if (key != null)
                    {
                        var backupDll = key.GetValue("LocalRDP_Backup")?.ToString();
                        if (backupDll != null)
                        {
                            key.SetValue("AudioEnumeratorDll", backupDll, RegistryValueKind.String);
                            key.DeleteValue("LocalRDP_Backup", false);
                        }
                        else
                        {
                            key.SetValue("AudioEnumeratorDll", "rdpendp.dll", RegistryValueKind.String);
                        }
                    }
                }

                // 3. 恢复服务类型为默认共享进程 (0x20) 并恢复拆分进程策略，同时确保服务未被禁用
                using (RegistryKey? svcKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TermService", true))
                {
                    if (svcKey != null)
                    {
                        svcKey.SetValue("Type", 0x20, RegistryValueKind.DWord);
                        svcKey.DeleteValue("SvcHostSplitDisable", false);
                        
                        var startVal = svcKey.GetValue("Start");
                        if (startVal != null && (int)startVal == 4)
                        {
                            svcKey.SetValue("Start", 3, RegistryValueKind.DWord);
                            Logger.LogInfo("检测到 TermService 启动类型被禁用，已恢复为手动启动 (3)。");
                        }
                    }
                }

                // 给系统一定的缓冲时间，确保 SCM 完成注册表和进程句柄的清理与同步
                Logger.LogInfo("等待系统同步服务配置...");
                Thread.Sleep(1500);

                // 4. 重新启动服务
                ControlService("TermService", stop: false);

                // 5. 尝试删除 System32 目录下的 DLL 文件
                string system32Path = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string zydisSystem32Path = Path.Combine(system32Path, "Zydis.dll");
                string endpSystem32Path = Path.Combine(system32Path, "EndpWrap.dll");

                try
                {
                    if (File.Exists(zydisSystem32Path))
                    {
                        File.Delete(zydisSystem32Path);
                        Logger.LogInfo("已成功清理 System32 下的 Zydis.dll。");
                    }
                }
                catch (Exception zydisEx)
                {
                    Logger.LogWarning($"清理 System32 下的 Zydis.dll 失败 (可能被占用，重启后可删除): {zydisEx.Message}");
                }

                try
                {
                    if (File.Exists(endpSystem32Path))
                    {
                        File.Delete(endpSystem32Path);
                        Logger.LogInfo("已成功清理 System32 下的 EndpWrap.dll。");
                    }
                }
                catch (Exception endpEx)
                {
                    Logger.LogWarning($"清理 System32 下的 EndpWrap.dll 失败: {endpEx.Message}");
                }

                // 6. 尝试删除临时文件与目录（由于 DLL 可能会被进程锁住，若失败提示重启是正常的）
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

                // 7. 清理 GPU 渲染保活策略
                RemoveGpuKeepAlivePolicies();

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
        /// 强制注销当前系统中所有活跃的 RDP 远程会话（非 console/services），
        /// 确保 TermService 服务可以被正常停止。
        /// </summary>
        private static void KillAllRdpSessions()
        {
            try
            {
                Logger.LogInfo("正在枚举并强制注销所有活跃 RDP 会话...");

                // 执行 qwinsta 获取所有会话列表
                var psi = new ProcessStartInfo("qwinsta")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.GetEncoding(0)
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return;
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);

                    // 逐行解析 qwinsta 的输出，格式：SESSIONNAME  USERNAME  ID  STATE  TYPE  DEVICE
                    foreach (string line in output.Split('\n'))
                    {
                        // 跳过标题行和空行
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        if (line.Contains("SESSIONNAME", StringComparison.OrdinalIgnoreCase) || line.Contains("会话名"))
                            continue;

                        // 跳过 console 和 services 会话（这两个是系统核心会话，不应注销）
                        if (line.Contains("console", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("services", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // 替换 '>' 为空格，避免会话名与前缀粘连导致 tokens 错位
                        string processedLine = line.Replace('>', ' ');

                        string[] tokens = processedLine.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        int stateIndex = -1;
                        for (int i = 0; i < tokens.Length; i++)
                        {
                            string t = tokens[i];
                            if (string.Equals(t, "Active", StringComparison.OrdinalIgnoreCase) || t == "运行中" ||
                                string.Equals(t, "Disc", StringComparison.OrdinalIgnoreCase) || 
                                string.Equals(t, "Disconnected", StringComparison.OrdinalIgnoreCase) || 
                                t == "断开")
                            {
                                stateIndex = i;
                                break;
                            }
                        }

                        if (stateIndex >= 1)
                        {
                            string idStr = tokens[stateIndex - 1];
                            if (int.TryParse(idStr, out int sessionId) && sessionId > 0)
                            {
                                Logger.LogInfo($"正在注销会话 ID={sessionId}...");
                                var logoffPsi = new ProcessStartInfo("logoff", sessionId.ToString())
                                {
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                };
                                using (var logoffProc = Process.Start(logoffPsi))
                                {
                                    logoffProc?.WaitForExit(5000);
                                }
                            }
                        }
                    }
                }

                // 等待 2 秒，让系统完成会话清理后再停止服务
                Thread.Sleep(2000);
                Logger.LogInfo("活跃 RDP 会话已清理完毕。");
            }
            catch (Exception ex)
            {
                // 会话注销失败不应阻止后续部署，记录警告并继续
                Logger.LogWarning($"清理活跃 RDP 会话时出错（将继续尝试停止服务）: {ex.Message}");
            }
        }

        /// <summary>
        /// 控制服务的停止与启动。停止时若超时则自动使用 taskkill 强制终止承载进程。
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
                        try
                        {
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        }
                        catch (System.ServiceProcess.TimeoutException)
                        {
                            Logger.LogWarning($"服务 {serviceName} 在 30 秒内未能正常停止，正在强制终止承载进程...");
                            ForceKillService(serviceName);
                        }
                    }
                }
                else
                {
                    if (sc.Status != ServiceControllerStatus.Running && sc.Status != ServiceControllerStatus.StartPending)
                    {
                        int retry = 3;
                        while (retry > 0)
                        {
                            try
                            {
                                Logger.LogInfo($"正在尝试启动服务 {serviceName}...");
                                sc.Start();
                                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                                Logger.LogInfo($"服务 {serviceName} 启动成功。");
                                break;
                            }
                            catch (Exception ex)
                            {
                                retry--;
                                if (retry <= 0)
                                {
                                    Logger.LogError($"启动服务 {serviceName} 失败，已耗尽重试次数。", ex);
                                    throw;
                                }
                                Logger.LogWarning($"启动服务 {serviceName} 遇到暂时性错误: {ex.Message}。将在 1.5 秒后重试 (剩余 {retry} 次)...");
                                Thread.Sleep(1500);
                                sc.Refresh();
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 当服务无法在超时时间内正常停止时，使用 taskkill 强制终止承载该服务的进程
        /// </summary>
        private static void ForceKillService(string serviceName)
        {
            var psi = new ProcessStartInfo("taskkill", $"/f /fi \"services eq {serviceName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var proc = Process.Start(psi))
            {
                if (proc != null)
                {
                    proc.WaitForExit(10000);
                    Logger.LogInfo($"强制终止服务 {serviceName} 的承载进程，退出码: {proc.ExitCode}");
                }
            }
            // 等待系统释放文件句柄和资源锁
            Thread.Sleep(3000);
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

                // 禁用 URCP 协议以解决本地回环 RDP 连接黑屏/空白画面问题
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Terminal Server Client", true))
                {
                    key.SetValue("UseURCP", 0, RegistryValueKind.DWord);
                    Logger.LogInfo("已在注册表中禁用 RDP 客户端 URCP 协议以防止黑屏。");
                }

                // 配置凭据分配与免密登录系统组策略
                ApplyCredentialsDelegationPolicies();

                // 配置远程桌面连接的 GPU 渲染保活策略
                ApplyGpuKeepAlivePolicies();
            }
            catch
            {
                // 容错处理
            }
        }

        /// <summary>
        /// 配置远程桌面连接的 GPU 渲染保活策略，以保证 RDP 会话断开时 GPU 硬件渲染不受限制且分辨率不降为0
        /// </summary>
        private static void ApplyGpuKeepAlivePolicies()
        {
            try
            {
                Logger.LogInfo("开始配置 RDP 显卡渲染保活组策略...");

                // 1. HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", true))
                {
                    key.SetValue("fEnableRemoteFXAdvancedRemoteApp", 1, RegistryValueKind.DWord);
                    key.SetValue("bEnumerateHWBeforeSW", 1, RegistryValueKind.DWord);
                    key.SetValue("AVC444ModePreferred", 1, RegistryValueKind.DWord);
                    key.SetValue("AVCHardwareEncodePreferred", 1, RegistryValueKind.DWord);
                }

                // 2. HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", true))
                {
                    key.SetValue("fEnableRemoteFXAdvancedRemoteApp", 1, RegistryValueKind.DWord);
                    key.SetValue("MaxMonitors", 1, RegistryValueKind.DWord);
                    key.SetValue("MaxXResolution", 1920, RegistryValueKind.DWord);
                    key.SetValue("MaxYResolution", 1080, RegistryValueKind.DWord);
                }

                // 3. HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", true))
                {
                    key.SetValue("DisableAutoDisableGPU", 1, RegistryValueKind.DWord);
                }

                Logger.LogInfo("RDP 显卡渲染保活组策略配置完成。");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"配置 RDP 显卡渲染保活组策略失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理 GPU 渲染保活策略
        /// </summary>
        private static void RemoveGpuKeepAlivePolicies()
        {
            try
            {
                Logger.LogInfo("开始清理 RDP 显卡渲染保活组策略...");

                // 1. HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", true))
                {
                    if (key != null)
                    {
                        key.DeleteValue("fEnableRemoteFXAdvancedRemoteApp", false);
                        key.DeleteValue("bEnumerateHWBeforeSW", false);
                        key.DeleteValue("AVC444ModePreferred", false);
                        key.DeleteValue("AVCHardwareEncodePreferred", false);
                    }
                }

                // 2. HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", true))
                {
                    if (key != null)
                    {
                        key.DeleteValue("fEnableRemoteFXAdvancedRemoteApp", false);
                        key.DeleteValue("MaxMonitors", false);
                        key.DeleteValue("MaxXResolution", false);
                        key.DeleteValue("MaxYResolution", false);
                    }
                }

                // 3. HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", true))
                {
                    if (key != null)
                    {
                        key.DeleteValue("DisableAutoDisableGPU", false);
                    }
                }

                Logger.LogInfo("RDP 显卡渲染保活组策略清理完成。");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"清理 RDP 显卡渲染保活组策略失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 配置组策略注册表，启用凭据分配（CredSSP/NTLM）以及禁用强制密码输入，以解决自动登录问题
        /// </summary>
        public static void ApplyCredentialsDelegationPolicies()
        {
            try
            {
                Logger.LogInfo("开始配置系统凭据分配策略，以实现自动免密登录...");

                // 1. 允许分配保存的和默认的凭据
                string credDelPath = @"SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation";
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(credDelPath, true))
                {
                    key.SetValue("AllowSavedCredentials", 1, RegistryValueKind.DWord);
                    key.SetValue("AllowSavedCredentialsWhenNTLMOnly", 1, RegistryValueKind.DWord);
                    key.SetValue("AllowDefCredentials", 1, RegistryValueKind.DWord);
                    key.SetValue("AllowDefCredentialsWhenNTLMOnly", 1, RegistryValueKind.DWord);
                }

                // 2. 为这些策略注册 TERMSRV/* 目标 (允许分配到任何远程桌面服务)
                string[] subKeys = new string[] {
                    "AllowSavedCredentials",
                    "AllowSavedCredentialsWhenNTLMOnly",
                    "AllowDefCredentials",
                    "AllowDefCredentialsWhenNTLMOnly"
                };

                foreach (var sub in subKeys)
                {
                    using (RegistryKey subKey = Registry.LocalMachine.CreateSubKey($@"{credDelPath}\{sub}", true))
                    {
                        subKey.SetValue("1", @"TERMSRV/*", RegistryValueKind.String);
                    }
                }

                // 3. 禁用远程连接的“始终要求输入密码”限制 (组策略级别)
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", true))
                {
                    key.SetValue("fPromptForPassword", 0, RegistryValueKind.DWord);
                }

                // 4. 禁用远程连接的“始终要求输入密码”限制 (本地会话配置级别)
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", true))
                {
                    key.SetValue("fPromptForPassword", 0, RegistryValueKind.DWord);
                }

                // 5. 启用单一会话限制 (关键修复：确保同一个账号在断开后能重连到原会话，而不是无限创建新会话)
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server", true))
                {
                    key.SetValue("fSingleSessionPerUser", 1, RegistryValueKind.DWord);
                }
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services", true))
                {
                    key.SetValue("fSingleSessionPerUser", 1, RegistryValueKind.DWord);
                }

                Logger.LogInfo("系统凭据分配策略、免密连接及单用户多会话组策略配置成功。");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"配置系统凭据分配与单用户多会话策略失败: {ex.Message}");
            }
        }
    }
}
