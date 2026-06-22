using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rdpManager.Helpers;
using System.Threading.Tasks;

namespace rdpManager
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _refreshTimer;
        private bool _isPasswordShown = false;
        private bool _isNewUserPasswordShown = false;
        private string _currentPassword = string.Empty;

        // 系统托盘相关
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isExplicitExit = false;
        private bool _hasInitializedExpanderState = false;

        // 阻止睡眠 API
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        // Win32 API 辅助方法，用于保活断开时隐藏 RDP 窗口，恢复连接时显示
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private class RdpSessionProcess
        {
            public string Username { get; set; } = string.Empty;
            public Process? Process { get; set; }
            public bool IsHidden { get; set; }
            public List<IntPtr> TargetWindows { get; set; } = new List<IntPtr>();
        }

        private readonly List<RdpSessionProcess> _rdpProcesses = new List<RdpSessionProcess>();

        private static List<IntPtr> GetWindowHandlesByPid(int pid)
        {
            var windowHandles = new List<IntPtr>();
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint processId);
                    if (processId == pid)
                    {
                        windowHandles.Add(hWnd);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return windowHandles;
        }

        private string GetCommandLineOfProcess(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo("wmic", $"process where ProcessId={pid} get CommandLine")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(1000);
                        return output;
                    }
                }
            }
            catch { }
            return "";
        }

        private Process? FindMstscProcessForUser(string username)
        {
            try
            {
                var procs = Process.GetProcessesByName("mstsc");
                foreach (var p in procs)
                {
                    try
                    {
                        string cmdline = GetCommandLineOfProcess(p.Id);
                        if (cmdline.Contains($"rdp_connection_{username}.rdp") || cmdline.Contains($"rdp_connection_{username}"))
                        {
                            return p;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"查找用户 '{username}' 的 mstsc 进程失败: {ex.Message}");
            }
            return null;
        }

        private void ApplyRdpClientKeepAliveRegistry()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Terminal Server Client", true))
                {
                    key.SetValue("RemoteDesktopServicesSessionByProtocol", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
                Logger.LogInfo("已应用本地客户端后台保活注册表优化(RemoteDesktopServicesSessionByProtocol=1)。");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"应用本地客户端保活注册表优化失败: {ex.Message}");
            }
        }

        private bool IsSessionActive(string username)
        {
            if (ListSessions.ItemsSource is IEnumerable<SessionItem> items)
            {
                var item = items.FirstOrDefault(i => i.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    return item.StateText.Contains("活跃");
                }
            }
            return false;
        }

        private bool TryShowExistingRdpWindow(string username)
        {
            lock (_rdpProcesses)
            {
                _rdpProcesses.RemoveAll(p => p.Process == null || p.Process.HasExited);

                var rdpProc = _rdpProcesses.FirstOrDefault(p => p.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (rdpProc == null)
                {
                    var extProc = FindMstscProcessForUser(username);
                    if (extProc != null)
                    {
                        rdpProc = new RdpSessionProcess
                        {
                            Username = username,
                            Process = extProc,
                            IsHidden = true
                        };
                        _rdpProcesses.Add(rdpProc);
                    }
                }

                if (rdpProc != null && rdpProc.Process != null && !rdpProc.Process.HasExited)
                {
                    // 如果记录了目标窗口，只显示这些窗口
                    if (rdpProc.TargetWindows != null && rdpProc.TargetWindows.Count > 0)
                    {
                        foreach (var hwnd in rdpProc.TargetWindows)
                        {
                            ShowWindow(hwnd, SW_SHOW);
                        }
                    }
                    else
                    {
                        // 退化处理：如果没有记录，只显示当前不可见的窗口或者主窗口
                        // 但由于此时可能已经是隐藏状态，无法判断之前是否可见，
                        // mstsc 的主窗口通常带有特定的样式或行为，这里尽量保守
                        var hwnds = GetWindowHandlesByPid(rdpProc.Process.Id);
                        foreach (var hwnd in hwnds)
                        {
                            // 暴力恢复可能会导致白块，但作为外部进程的 fallback 只能尽量
                            ShowWindow(hwnd, SW_SHOW);
                        }
                    }
                    rdpProc.IsHidden = false;
                    rdpProc.TargetWindows?.Clear();
                    Logger.LogInfo($"已从后台恢复并显示用户 '{username}' 的 RDP 窗口。");
                    return true;
                }
            }
            return false;
        }

        private void KillExistingMstscProcessForUser(string username)
        {
            lock (_rdpProcesses)
            {
                var rdpProc = _rdpProcesses.FirstOrDefault(p => p.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (rdpProc != null)
                {
                    try
                    {
                        if (rdpProc.Process != null && !rdpProc.Process.HasExited)
                        {
                            rdpProc.Process.Kill();
                        }
                    }
                    catch { }
                    _rdpProcesses.Remove(rdpProc);
                }
            }

            var externalProc = FindMstscProcessForUser(username);
            if (externalProc != null)
            {
                try { externalProc.Kill(); } catch { }
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            ComboTheme.SelectedIndex = (int)ThemeManager.CurrentMode;

            // 订阅系统日志，并在界面展示
            Logger.OnLogWritten += Logger_OnLogWritten;

            // 加载初始诊断状态
            RefreshDiagnosticStatus();

            // 加载分辨率和缩放默认选项
            ComboResolution.SelectedIndex = 0; // 自适应窗口
            ComboScale.SelectedIndex = 0;      // 100%

            // 加载账号列表
            LoadAccounts();

            // 初始化托盘
            InitializeNotifyIcon();

            // 初始化轮询定时器（每4秒刷新一次活跃会话和补丁状态）
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromSeconds(4);
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();

            // 应用主机级阻止休眠策略
            ApplySleepPrevention();

            // 应用本地客户端后台保活注册表优化
            ApplyRdpClientKeepAliveRegistry();

            // 在后台应用系统凭据分配和会话重连策略 (修复单用户多会话的遗留设置)
            Task.Run(() => TermWrapDeployer.ApplyCredentialsDelegationPolicies());

            Logger.LogInfo("LocalRDP 界面初始化成功，已就绪。");
        }

        private void Logger_OnLogWritten(string logLine)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLogOutput.AppendText(logLine);
                TxtLogOutput.ScrollToEnd();
            });
        }

        private void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            RefreshDiagnosticStatus();
            RefreshSessions();
            ApplySleepPrevention();
        }

        // ======================= 诊断与补丁管理 =======================

        private void RefreshDiagnosticStatus()
        {
            // 1. 判断提权状态
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            if (isAdmin)
            {
                AdminTag.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BadgeSuccessBgBrush"); // 浅绿
                AdminTagTxt.Text = "已提权";
                AdminTagTxt.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BadgeSuccessTextBrush");
                PrivilegeWarningAlert.Visibility = Visibility.Collapsed;
            }
            else
            {
                AdminTag.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BadgeDangerBgBrush"); // 浅红
                AdminTagTxt.Text = "受限运行";
                AdminTagTxt.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BadgeDangerTextBrush");
                PrivilegeWarningAlert.Visibility = Visibility.Visible;
            }

            // 2. 判断 RDP 服务状态和启用状态
            bool isRdpRunning = TermWrapDeployer.IsTermServiceRunning();
            bool isRdpEnabled = TermWrapDeployer.IsRdpFeatureEnabled();
            if (isRdpRunning && isRdpEnabled)
            {
                RdpTag.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BadgeSuccessBgBrush");
                RdpTagTxt.Text = "RDP开";
                RdpTagTxt.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BadgeSuccessTextBrush");
            }
            else
            {
                RdpTag.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BadgeDisabledBgBrush");
                RdpTagTxt.Text = isRdpEnabled ? "RDP关" : "RDP禁用";
                RdpTagTxt.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BadgeDisabledTextBrush");
            }

            // 控制警告横幅显示
            RdpDisabledWarningAlert.Visibility = isRdpEnabled ? Visibility.Collapsed : Visibility.Visible;

            // 3. 判断补丁激活状态
            bool isTermWrapActive = TermWrapDeployer.IsMultiSessionActive();
            bool isRdpWrapActive = TermWrapDeployer.IsRdpWrapActive();

            if (isTermWrapActive)
            {
                TermWrapStatusLabel.Text = "已启用";
                TermWrapStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusSuccessTextBrush");
                RdpWrapStatusLabel.Text = "未安装";
                RdpWrapStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusDisabledTextBrush");
            }
            else
            {
                if (isRdpWrapActive)
                {
                    TermWrapStatusLabel.Text = "未安装";
                    TermWrapStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusDisabledTextBrush");
                    RdpWrapStatusLabel.Text = "已启用";
                    RdpWrapStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusSuccessTextBrush");
                }
                else
                {
                    TermWrapStatusLabel.Text = "未安装";
                    TermWrapStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusErrorTextBrush");
                    RdpWrapStatusLabel.Text = "未安装";
                    RdpWrapStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusErrorTextBrush");
                }
            }

            bool isEndpWrapActive;
            if (!Environment.Is64BitOperatingSystem)
            {
                isEndpWrapActive = true; // x86 系统无 EndpWrap，视为不影响
                EndpWrapStatusLabel.Text = "不适用";
                EndpWrapStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusDisabledTextBrush");
            }
            else
            {
                isEndpWrapActive = TermWrapDeployer.IsEndpWrapActive();
                if (isEndpWrapActive)
                {
                    EndpWrapStatusLabel.Text = "已启用";
                    EndpWrapStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusSuccessTextBrush");
                }
                else
                {
                    EndpWrapStatusLabel.Text = "未安装";
                    EndpWrapStatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusErrorTextBrush");
                }
            }

            bool isInstalled = (isTermWrapActive || isRdpWrapActive) && isEndpWrapActive;
            PatchSuccessIcon.Visibility = isInstalled ? Visibility.Visible : Visibility.Collapsed;

            if (!_hasInitializedExpanderState)
            {
                ExpanderPatchManager.IsExpanded = !isInstalled;
                _hasInitializedExpanderState = true;
            }

            // 仅当补丁未安装且 RDP 已开启时，才允许安装补丁
            BtnInstallPatch.IsEnabled = !isInstalled && isRdpEnabled;
        }

        private async void BtnInstallPatch_Click(object sender, RoutedEventArgs e)
        {
            if (!TermWrapDeployer.IsRdpFeatureEnabled())
            {
                MessageBox.Show("系统远程桌面功能尚未开启！\n请前往“系统设置 -> 远程桌面”启用后再安装补丁。", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            BtnInstallPatch.IsEnabled = false;
            Logger.LogInfo("正在部署补丁及外设重定向注册表...");
            
            bool success = false;
            string error = string.Empty;

            try
            {
                var result = await System.Threading.Tasks.Task.Run(() =>
                {
                    bool deployResult = TermWrapDeployer.DeployPatch(out string err);
                    return new { Success = deployResult, Error = err };
                });
                success = result.Success;
                error = result.Error;
            }
            catch (Exception ex)
            {
                success = false;
                error = ex.Message;
                Logger.LogError("部署补丁发生异常", ex);
            }
            finally
            {
                BtnInstallPatch.IsEnabled = true;
                RefreshDiagnosticStatus();
            }

            if (success)
            {
                ExpanderPatchManager.IsExpanded = false;
                MessageBox.Show("TermWrap 并发与音频保活补丁部署成功！\n系统多会话并发及 RDP 优化策略已完美激活。", "部署成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"部署失败: {error}\n请确保以管理员身份运行程序并暂时关闭杀毒软件后重试。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnUninstallPatch_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("还原补丁将移除多路并发和音频保活劫持，使远程桌面还原为出厂设置。是否继续？", "确认还原", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            BtnUninstallPatch.IsEnabled = false;
            Logger.LogInfo("正在还原远程桌面原始配置并清理文件...");

            bool success = false;
            string error = string.Empty;

            try
            {
                var runResult = await System.Threading.Tasks.Task.Run(() =>
                {
                    bool uninstallResult = TermWrapDeployer.UninstallPatch(out string err);
                    return new { Success = uninstallResult, Error = err };
                });
                success = runResult.Success;
                error = runResult.Error;
            }
            catch (Exception ex)
            {
                success = false;
                error = ex.Message;
                Logger.LogError("卸载补丁异常", ex);
            }
            finally
            {
                BtnUninstallPatch.IsEnabled = true;
                RefreshDiagnosticStatus();
            }

            if (success)
            {
                ExpanderPatchManager.IsExpanded = true;
                if (string.IsNullOrEmpty(error))
                {
                    MessageBox.Show("补丁卸载成功，远程桌面已恢复到系统原始状态。", "还原成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(error, "还原提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                MessageBox.Show($"还原失败: {error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ======================= 账号管理 =======================

        private void LoadAccounts()
        {
            ComboAccounts.Items.Clear();
            try
            {
                var accounts = AccountHelper.GetLocalAccounts(true);
                foreach (var acc in accounts)
                {
                    ComboAccounts.Items.Add(acc.Name);
                }

                if (ComboAccounts.Items.Count > 0)
                {
                    ComboAccounts.SelectedIndex = 0;
                }
                else
                {
                    ComboAccounts.Items.Add("暂无系统用户");
                    ComboAccounts.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("加载本地账户列表失败", ex);
            }
        }

        private void ComboAccounts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string selected = ComboAccounts.SelectedItem as string ?? string.Empty;
            if (string.IsNullOrEmpty(selected) || selected == "暂无系统用户")
            {
                UpdatePasswordFields(string.Empty);
                BtnConnectVirtualDesktop.Content = "🔌 连接虚拟桌面";
                return;
            }

            BtnConnectVirtualDesktop.Content = $"🔌 以 {selected} 连接虚拟桌面";

            // 从凭证管理器中读取密码并填入
            if (CredentialHelper.GetCredential($"RDPManager:{selected}", out _, out string savedPwd))
            {
                UpdatePasswordFields(savedPwd);
            }
            else
            {
                UpdatePasswordFields(string.Empty);
            }
        }

        private void UpdatePasswordFields(string pwd)
        {
            _currentPassword = pwd;
            AccountPasswordBox.Password = pwd;
            AccountPasswordTxt.Text = pwd;
        }

        private void AccountPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_isPasswordShown)
            {
                _currentPassword = AccountPasswordBox.Password;
                AccountPasswordTxt.Text = _currentPassword;
            }
        }

        private void AccountPasswordTxt_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isPasswordShown)
            {
                _currentPassword = AccountPasswordTxt.Text;
                AccountPasswordBox.Password = _currentPassword;
            }
        }

        private void BtnShowHidePassword_Click(object sender, RoutedEventArgs e)
        {
            _isPasswordShown = !_isPasswordShown;
            if (_isPasswordShown)
            {
                BtnShowHidePassword.Content = "🙈";
                AccountPasswordBox.Visibility = Visibility.Collapsed;
                AccountPasswordTxt.Visibility = Visibility.Visible;
            }
            else
            {
                BtnShowHidePassword.Content = "👁️";
                AccountPasswordTxt.Visibility = Visibility.Collapsed;
                AccountPasswordBox.Visibility = Visibility.Visible;
            }
        }

        private void BtnNewUser_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // 防止触发展开折叠
            NewUserUsernameTxt.Text = string.Empty;
            NewUserPasswordTxt.Text = string.Empty;
            NewUserPasswordBox.Password = string.Empty;

            // 默认设置为隐藏密码状态
            _isNewUserPasswordShown = false;
            BtnNewUserShowHidePassword.Content = "👁️";
            NewUserPasswordTxt.Visibility = Visibility.Collapsed;
            NewUserPasswordBox.Visibility = Visibility.Visible;

            NewUserModal.Visibility = Visibility.Visible;
        }

        private void BtnCancelNewUser_Click(object sender, RoutedEventArgs e)
        {
            NewUserModal.Visibility = Visibility.Collapsed;
        }

        private void NewUserPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_isNewUserPasswordShown)
            {
                NewUserPasswordTxt.Text = NewUserPasswordBox.Password;
            }
        }

        private void NewUserPasswordTxt_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isNewUserPasswordShown)
            {
                NewUserPasswordBox.Password = NewUserPasswordTxt.Text;
            }
        }

        private void BtnNewUserShowHidePassword_Click(object sender, RoutedEventArgs e)
        {
            _isNewUserPasswordShown = !_isNewUserPasswordShown;
            if (_isNewUserPasswordShown)
            {
                BtnNewUserShowHidePassword.Content = "🙈";
                NewUserPasswordBox.Visibility = Visibility.Collapsed;
                NewUserPasswordTxt.Visibility = Visibility.Visible;
            }
            else
            {
                BtnNewUserShowHidePassword.Content = "👁️";
                NewUserPasswordTxt.Visibility = Visibility.Collapsed;
                NewUserPasswordBox.Visibility = Visibility.Visible;
            }
        }

        private void BtnGeneratePassword_Click(object sender, RoutedEventArgs e)
        {
            string strongPwd = GenerateStrongPassword();
            NewUserPasswordBox.Password = strongPwd;
            NewUserPasswordTxt.Text = strongPwd;
        }

        private async void BtnConfirmCreateUser_Click(object sender, RoutedEventArgs e)
        {
            string username = NewUserUsernameTxt.Text.Trim();
            string password = NewUserPasswordTxt.Text;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("账户名和密码不能为空。");
                return;
            }

            // 1. 切换至“创建中”状态，禁用控件，显示加载条
            NewUserUsernameTxt.IsEnabled = false;
            NewUserPasswordTxt.IsEnabled = false;
            NewUserPasswordBox.IsEnabled = false;
            BtnNewUserShowHidePassword.IsEnabled = false;
            BtnGeneratePassword.IsEnabled = false;
            BtnCancelNewUser.IsEnabled = false;
            BtnConfirmCreateUser.IsEnabled = false;
            BtnConfirmCreateUser.Content = "创建中...";
            NewUserLoadingPanel.Visibility = Visibility.Visible;

            Logger.LogInfo($"正在创建本地隔离账户 '{username}'...");
            bool success = false;
            string err = string.Empty;

            try
            {
                // 2. 异步执行耗时任务，保持 UI 线程流畅
                success = await Task.Run(() => AccountHelper.CreateRobotAccount(username, password, out err));
            }
            catch (Exception ex)
            {
                err = ex.Message;
                Logger.LogError("创建账号时出现未处理异常", ex);
            }

            // 3. 根据结果执行处理
            if (success)
            {
                // 成功时立即关闭模态框
                NewUserModal.Visibility = Visibility.Collapsed;

                // 恢复控件状态以备下次使用
                NewUserUsernameTxt.IsEnabled = true;
                NewUserPasswordTxt.IsEnabled = true;
                NewUserPasswordBox.IsEnabled = true;
                BtnNewUserShowHidePassword.IsEnabled = true;
                BtnGeneratePassword.IsEnabled = true;
                BtnCancelNewUser.IsEnabled = true;
                BtnConfirmCreateUser.IsEnabled = true;
                BtnConfirmCreateUser.Content = "确认创建";
                NewUserLoadingPanel.Visibility = Visibility.Collapsed;

                CredentialHelper.SaveCredential($"RDPManager:{username}", username, password);
                Logger.LogInfo($"本地隔离账户 '{username}' 创建成功。");
                MessageBox.Show($"账户 '{username}' 已创建成功，管理员特权及免密登录首登组策略已自动分配！", "创建成功", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadAccounts();
                // 自动选择新建的用户
                ComboAccounts.SelectedItem = username;
            }
            else
            {
                // 失败时保持模态框打开，恢复控件状态以便用户修改
                NewUserUsernameTxt.IsEnabled = true;
                NewUserPasswordTxt.IsEnabled = true;
                NewUserPasswordBox.IsEnabled = true;
                BtnNewUserShowHidePassword.IsEnabled = true;
                BtnGeneratePassword.IsEnabled = true;
                BtnCancelNewUser.IsEnabled = true;
                BtnConfirmCreateUser.IsEnabled = true;
                BtnConfirmCreateUser.Content = "确认创建";
                NewUserLoadingPanel.Visibility = Visibility.Collapsed;

                MessageBox.Show($"创建失败: {err}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExportLog_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // 防止触发展开折叠
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                    FileName = "LocalRDP_Log.txt",
                    DefaultExt = ".txt"
                };

                if (dialog.ShowDialog() == true)
                {
                    System.IO.File.WriteAllText(dialog.FileName, TxtLogOutput.Text);
                    MessageBox.Show("日志已成功导出。", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出日志失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDeleteAccount_Click(object sender, RoutedEventArgs e)
        {
            string selected = ComboAccounts.SelectedItem as string ?? string.Empty;
            if (string.IsNullOrEmpty(selected) || selected == "暂无系统用户")
            {
                MessageBox.Show("当前未选中有效账户！");
                return;
            }

            if (selected.Equals("Administrator", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("安全警告：禁止通过本工具删除系统内置管理员账户！", "禁止操作", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show($"警告：您确定要删除本地账户 '{selected}' 吗？\n删除后，该用户的会话、桌面配置及全部数据均将丢失！", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Stop);
            if (confirm != MessageBoxResult.Yes) return;

            Logger.LogInfo($"正在删除账户 '{selected}'...");
            try
            {
                var psi = new ProcessStartInfo("qwinsta")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.GetEncoding(0)
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(3000);
                        foreach (string line in output.Split('\n'))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            if (line.Contains("SESSIONNAME", StringComparison.OrdinalIgnoreCase) || line.Contains("会话名"))
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
                                if (int.TryParse(idStr, out int sessionId))
                                {
                                    string username = "";
                                    if (stateIndex >= 2)
                                    {
                                        string candidate = tokens[stateIndex - 2];
                                        if (!candidate.StartsWith("rdp-tcp", StringComparison.OrdinalIgnoreCase) && 
                                            !candidate.Equals("services", StringComparison.OrdinalIgnoreCase) &&
                                            !candidate.Equals("console", StringComparison.OrdinalIgnoreCase))
                                        {
                                            username = candidate;
                                        }
                                    }
                                    
                                    if (username.Equals(selected, StringComparison.OrdinalIgnoreCase))
                                    {
                                        Logger.LogInfo($"检测到待删除用户 '{selected}' 存在会话 (SessionId={sessionId})，正在强制注销以释放锁定...");
                                        var logoffPsi = new ProcessStartInfo("logoff", sessionId.ToString())
                                        {
                                            UseShellExecute = false,
                                            CreateNoWindow = true
                                        };
                                        using (var logoffProc = Process.Start(logoffPsi))
                                        {
                                            logoffProc?.WaitForExit(3000);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"检测并清理用户会话时发生异常: {ex.Message}");
            }

            try
            {
                bool success = AccountHelper.DeleteRobotAccount(selected, out string err);
                if (success)
                {
                    Logger.LogInfo($"账户 '{selected}' 已成功从系统中移除。");
                    MessageBox.Show($"账户 '{selected}' 已成功删除。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadAccounts();
                }
                else
                {
                    MessageBox.Show($"删除失败: {err}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("删除账号时出现异常", ex);
                MessageBox.Show($"删除账号失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GenerateStrongPassword()
        {
            const string uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            const string lowercase = "abcdefghijklmnopqrstuvwxyz";
            const string numbers = "0123456789";
            const string symbols = "!@#%^&*_+-=";
            
            var random = new Random();
            var passwordChars = new char[12];
            
            // 确保各类字符至少包含一个
            passwordChars[0] = uppercase[random.Next(uppercase.Length)];
            passwordChars[1] = lowercase[random.Next(lowercase.Length)];
            passwordChars[2] = numbers[random.Next(numbers.Length)];
            passwordChars[3] = symbols[random.Next(symbols.Length)];
            
            string allChars = uppercase + lowercase + numbers + symbols;
            for (int i = 4; i < 12; i++)
            {
                passwordChars[i] = allChars[random.Next(allChars.Length)];
            }
            
            // 打乱顺序
            return new string(passwordChars.OrderBy(x => random.Next()).ToArray());
        }

        // ======================= 活跃会话监测 =======================

        private void RefreshSessions()
        {
            try
            {
                var sessions = new List<SessionItem>();
                
                var psi = new ProcessStartInfo("qwinsta")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.GetEncoding(0)
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(3000);

                        foreach (string line in output.Split('\n'))
                        {
                            if (string.IsNullOrWhiteSpace(line))
                                continue;

                            // 过滤表头 (支持中英文)
                            if (line.Contains("SESSIONNAME", StringComparison.OrdinalIgnoreCase) || line.Contains("会话名"))
                                continue;

                            // 仅过滤 services 核心会话（保留 console）
                            if (line.Contains("services", StringComparison.OrdinalIgnoreCase))
                                continue;

                            // 替换 '>' 为空格，避免会话名与前缀粘连导致 tokens 错位
                            string processedLine = line.Replace('>', ' ');

                            string[] tokens = processedLine.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            int stateIndex = -1;
                            bool isActive = false;

                            for (int i = 0; i < tokens.Length; i++)
                            {
                                string t = tokens[i];
                                if (string.Equals(t, "Active", StringComparison.OrdinalIgnoreCase) || t == "运行中")
                                {
                                    stateIndex = i;
                                    isActive = true;
                                    break;
                                }
                                else if (string.Equals(t, "Disc", StringComparison.OrdinalIgnoreCase) || 
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
                                if (int.TryParse(idStr, out int sessionId))
                                {
                                    bool isConsole = processedLine.Contains("console", StringComparison.OrdinalIgnoreCase);
                                    string username = isConsole ? "console" : "RDP回环会话";
                                    if (stateIndex >= 2)
                                    {
                                        string candidate = tokens[stateIndex - 2];
                                        if (!candidate.StartsWith("rdp-tcp", StringComparison.OrdinalIgnoreCase) && 
                                            !candidate.Equals("services", StringComparison.OrdinalIgnoreCase))
                                        {
                                            username = candidate;
                                        }
                                    }

                                    // 如果是当前物理控制台登录账号，或者正好是当前系统的 Windows 用户，标记 IsCurrentUser
                                    bool isCurrentUser = string.Equals(username, Environment.UserName, StringComparison.OrdinalIgnoreCase);

                                    sessions.Add(new SessionItem
                                    {
                                        SessionId = sessionId,
                                        Username = username,
                                        StateText = isActive ? "🟢 活跃" : "🟡 断开",
                                        DurationText = "保活中",
                                        IsConsole = isConsole,
                                        IsCurrentUser = isCurrentUser
                                    });
                                }
                            }
                        }
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    ListSessions.ItemsSource = sessions;
                    SessionCountBadge.Text = sessions.Count.ToString();
                    NoSessionsPlaceholder.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            }
            catch (Exception ex)
            {
                // 静默容错或打印到日志，不打扰用户
                Logger.LogWarning($"轮询刷新系统会话状态出错: {ex.Message}");
            }
        }

        private void BtnDisconnectSession_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int sessionId)
            {
                bool keepResolution = ChkKeepResolution.IsChecked == true;
                string username = "";
                if (ListSessions.ItemsSource is IEnumerable<SessionItem> items)
                {
                    var item = items.FirstOrDefault(i => i.SessionId == sessionId);
                    if (item != null)
                    {
                        username = item.Username;
                    }
                }

                Logger.LogInfo($"正在尝试将系统 RDP 会话 {sessionId} (用户: {username}) {(keepResolution ? "保活断开" : "断开挂起")}...");
                try
                {
                    if (keepResolution)
                    {
                        // 1. 优先尝试寻找本地的 RDP 客户端窗口进程并将其隐藏，保留其在后台活动并继续进行 GPU 渲染，不打扰当前物理控制台。
                        Process? mstscProc = null;
                        lock (_rdpProcesses)
                        {
                            _rdpProcesses.RemoveAll(p => p.Process == null || p.Process.HasExited);
                            var match = _rdpProcesses.FirstOrDefault(p => p.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                            if (match != null) mstscProc = match.Process;
                        }

                        if (mstscProc == null)
                        {
                            mstscProc = FindMstscProcessForUser(username);
                        }

                        bool hiddenSuccess = false;
                        if (mstscProc != null && !mstscProc.HasExited)
                        {
                            var hwnds = GetWindowHandlesByPid(mstscProc.Id);
                            var visibleHwnds = new List<IntPtr>();
                            if (hwnds.Count > 0)
                            {
                                foreach (var hwnd in hwnds)
                                {
                                    if (IsWindowVisible(hwnd))
                                    {
                                        visibleHwnds.Add(hwnd);
                                        ShowWindow(hwnd, SW_HIDE);
                                    }
                                }
                                lock (_rdpProcesses)
                                {
                                    var match = _rdpProcesses.FirstOrDefault(p => p.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                                    if (match != null)
                                    {
                                        match.IsHidden = true;
                                        match.TargetWindows = visibleHwnds;
                                    }
                                    else
                                    {
                                        _rdpProcesses.Add(new RdpSessionProcess
                                        {
                                            Username = username,
                                            Process = mstscProc,
                                            IsHidden = true,
                                            TargetWindows = visibleHwnds
                                        });
                                    }
                                }
                                hiddenSuccess = true;
                                Logger.LogInfo($"已成功隐藏用户 '{username}' 的远程桌面窗口进行后台保活。当前物理控制台 A 未受任何影响。");
                            }
                        }

                        // 2. 如果未能在本地查找到可隐藏的 RDP 窗口进程，则执行 fallback 降级机制：重定向到物理控制台
                        if (!hiddenSuccess)
                        {
                            Logger.LogInfo($"未检测到本地可用的客户端窗口进程，正在执行降级保活机制 (重定向至物理控制台)...");

                            // 先正常断开，防止阻塞
                            var tsdisconPsi = new ProcessStartInfo("tsdiscon", sessionId.ToString())
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using (var proc = Process.Start(tsdisconPsi))
                            {
                                proc?.WaitForExit(3000);
                            }

                            // SYSTEM 权限重定向
                            string taskName = "RdpKeepAliveTask_" + Guid.NewGuid().ToString("N");
                            string cmd = $"tscon {sessionId} /dest:console";
                            
                            var createPsi = new ProcessStartInfo("schtasks", $"/create /ru \"SYSTEM\" /sc once /st 00:00 /tn \"{taskName}\" /tr \"{cmd}\" /f") { CreateNoWindow = true, UseShellExecute = false };
                            Process.Start(createPsi)?.WaitForExit(3000);

                            var runPsi = new ProcessStartInfo("schtasks", $"/run /tn \"{taskName}\"") { CreateNoWindow = true, UseShellExecute = false };
                            Process.Start(runPsi)?.WaitForExit(3000);

                            var deletePsi = new ProcessStartInfo("schtasks", $"/delete /tn \"{taskName}\" /f") { CreateNoWindow = true, UseShellExecute = false };
                            Process.Start(deletePsi)?.WaitForExit(3000);

                            Logger.LogInfo($"已发送 RDP 会话 {sessionId} 重定向至 Console 指令 (SYSTEM 权限)。");

                            // 获取目标分辨率并执行 PowerShell 调整活动控制台分辨率
                            int width = 1920;
                            int height = 1080;
                            if (ComboResolution.SelectedItem is ComboBoxItem resItem && resItem.Tag is string resTag)
                            {
                                if (resTag != "0x0" && resTag.Contains("x"))
                                {
                                    var parts = resTag.Split('x');
                                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                                    {
                                        width = w;
                                        height = h;
                                    }
                                }
                            }

                            Task.Run(async () =>
                            {
                                await Task.Delay(1500);
                                try
                                {
                                    Logger.LogInfo($"正在为物理控制台强制锁定分辨率为: {width}x{height}...");
                                    string psCommand = $"try {{ Set-DisplayResolution -Width {width} -Height {height} -Force -ErrorAction Stop }} catch {{ " +
                                                       $"$code = 'using System; using System.Runtime.InteropServices; [StructLayout(LayoutKind.Sequential)] public struct DEVMODE {{ [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra; public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput; public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public short dmLogPixels; public short dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight; public int dmDisplayFlags; public int dmNup; public int dmDisplayFrequency; }} public class Display {{ [DllImport(\"user32.dll\")] public static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags); }}'; " +
                                                       $"Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue; " +
                                                       $"$devmode = New-Object DEVMODE; $devmode.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf($devmode); $devmode.dmFields = 0x00080000 -bor 0x00100000; $devmode.dmPelsWidth = {width}; $devmode.dmPelsHeight = {height}; " +
                                                       $"[Display]::ChangeDisplaySettings([ref]$devmode, 0) }}";

                                    var psPsi = new ProcessStartInfo("powershell", $"-NoProfile -WindowStyle Hidden -Command \"{psCommand}\"")
                                    {
                                        UseShellExecute = false,
                                        CreateNoWindow = true
                                    };
                                    using (var psProc = Process.Start(psPsi))
                                    {
                                        psProc?.WaitForExit(5000);
                                    }
                                    Logger.LogInfo($"物理控制台分辨率恢复指令已执行完毕。");
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogWarning($"执行分辨率恢复脚本失败: {ex.Message}");
                                }
                            });
                        }
                    }
                    else
                    {
                        // 普通挂起，只执行 tsdiscon
                        var psi = new ProcessStartInfo("tsdiscon", sessionId.ToString())
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var proc = Process.Start(psi))
                        {
                            proc?.WaitForExit(3000);
                        }

                        // 既然是普通挂起，也要关闭本地客户端
                        KillExistingMstscProcessForUser(username);
                        Logger.LogInfo($"已发送 RDP 会话 {sessionId} 普通挂起指令，并清理本地窗口进程。");
                    }

                    RefreshSessions();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"操作失败: {ex.Message}");
                }
            }
        }

        private void BtnLogoffSession_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int sessionId)
            {
                var confirm = MessageBox.Show($"您即将强制注销会话 {sessionId}。这会导致该会话下的所有程序（包括 RPA 机器人）立即终止且不会保存数据！是否继续？", "注销确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                Logger.LogInfo($"正在注销 RDP 会话 {sessionId}...");
                try
                {
                    var psi = new ProcessStartInfo("logoff", sessionId.ToString())
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var proc = Process.Start(psi))
                    {
                        proc?.WaitForExit(3000);
                    }
                    Logger.LogInfo($"已发送 RDP 会话 {sessionId} 强行注销指令。");
                    RefreshSessions();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"操作失败: {ex.Message}");
                }
            }
        }

        // ======================= 主机睡眠保活 =======================

        private void ApplySleepPrevention()
        {
            try
            {
                bool preventSleep = ChkPreventSleep.IsChecked == true;
                if (preventSleep)
                {
                    // ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
                    // 指令告诉 Windows 不要关闭屏幕 and 进入休眠
                    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                }
                else
                {
                    // 恢复 Windows 默认节电管理
                    SetThreadExecutionState(ES_CONTINUOUS);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"配置主机睡眠保活策略失败: {ex.Message}");
            }
        }

        // ======================= 连接虚拟桌面 =======================

        private void BtnConnectVirtualDesktop_Click(object sender, RoutedEventArgs e)
        {
            string username = ComboAccounts.SelectedItem as string ?? string.Empty;
            string password = _currentPassword;

            if (string.IsNullOrEmpty(username) || username == "暂无系统用户")
            {
                MessageBox.Show("请指定有效的本地隔离账户或在上方添加一个新账号！");
                return;
            }

            LaunchRdpConnection(username, password);
        }

        private void BtnOpenSession_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string username)
            {
                Logger.LogInfo($"尝试从会话列表直接打开桌面会话: Username={username}");
                
                // 从凭证管理器中读取密码并连入会话
                if (CredentialHelper.GetCredential($"RDPManager:{username}", out _, out string savedPwd))
                {
                    LaunchRdpConnection(username, savedPwd);
                }
                else
                {
                    MessageBox.Show($"未找到本地账户 '{username}' 的密码凭证，请先在系统账号设置中选中该账户并输入密码以进行缓存，然后再试。", "未缓存密码", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void LaunchRdpConnection(string username, string password, bool forceNew = false)
        {
            string server = "127.0.0.2"; // 并发连接本机回环 IP
            
            // 查找现有的该用户会话
            SessionItem? existingSession = null;
            if (ListSessions.ItemsSource is IEnumerable<SessionItem> items)
            {
                existingSession = items.FirstOrDefault(i => 
                    !i.IsConsole && i.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
                );
            }

            // 智能策略：如果不是强制新开，且有活跃会话，直接恢复 mstsc 窗口
            if (!forceNew && existingSession != null && existingSession.StateText.Contains("活跃"))
            {
                if (TryShowExistingRdpWindow(username))
                {
                    return;
                }
            }
            
            // 智能策略：如果该用户存在已断开的旧会话，自动将其 logoff 以免新建连接冲突
            if (existingSession != null && existingSession.StateText.Contains("断开"))
            {
                Logger.LogInfo($"检测到用户 '{username}' 存在已断开的旧会话 (SessionId={existingSession.SessionId})，正在自动注销以避免冲突...");
                try
                {
                    var logoffPsi = new ProcessStartInfo("logoff", existingSession.SessionId.ToString())
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var logoffProc = Process.Start(logoffPsi))
                    {
                        logoffProc?.WaitForExit(3000);
                    }
                    // 等待 1 秒确保注销清理工作完成
                    System.Threading.Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"注销旧会话失败: {ex.Message}");
                }
            }
            else if (!forceNew)
            {
                // 如果没有检测到已有会话，清理残留的本地 mstsc 进程
                KillExistingMstscProcessForUser(username);
            }

            // 写入 Windows 凭据管理器，免除 mstsc 的密码输入提示
            Logger.LogInfo($"正在向 Windows 凭据管理器注入凭据: Server={server}, Username={username}");
            try
            {
                var cmdkeyPsi = new ProcessStartInfo("cmdkey")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    Arguments = $"/generic:TERMSRV/{server} /user:{username} /pass:\"{password}\""
                };
                using (var proc = Process.Start(cmdkeyPsi))
                {
                    proc?.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("注入凭据出错", ex);
            }

            // 构建临时 RDP 配置文件
            Logger.LogInfo("正在构建临时 RDP 配置文件并调优连接参数...");
            try
            {
                string tempRdpPath = Path.Combine(Path.GetTempPath(), $"rdp_connection_{username}.rdp");
                var rdpContent = new System.Text.StringBuilder();

                rdpContent.AppendLine($"full address:s:{server}");
                rdpContent.AppendLine($"username:s:{username}");
                
                // 隐藏凭据和证书警告
                rdpContent.AppendLine("prompt for credentials:i:0");
                rdpContent.AppendLine("authentication level:i:0");

                // 分辨率配置
                if (ComboResolution.SelectedItem is ComboBoxItem resItem && resItem.Tag is string resTag)
                {
                    if (resTag != "0x0" && resTag.Contains("x"))
                    {
                        var parts = resTag.Split('x');
                        rdpContent.AppendLine("screen mode id:i:1"); // 窗口模式
                        rdpContent.AppendLine($"desktopwidth:i:{parts[0]}");
                        rdpContent.AppendLine($"desktopheight:i:{parts[1]}");
                    }
                    else
                    {
                        rdpContent.AppendLine("screen mode id:i:2"); // 全屏模式
                    }
                }

                // 缩放配置
                if (ComboScale.SelectedItem is ComboBoxItem scaleItem && scaleItem.Tag is string scaleTag)
                {
                    rdpContent.AppendLine($"desktopscalefactor:i:{scaleTag}");
                }

                // 重定向配置
                rdpContent.AppendLine($"redirectclipboard:i:{(ChkClipboard.IsChecked == true ? 1 : 0)}");
                rdpContent.AppendLine($"audiomode:i:{(ChkAudio.IsChecked == true ? 0 : 2)}"); // 2 = 静音保活
                rdpContent.AppendLine($"redirectsmartcards:i:{(ChkMicrophone.IsChecked == true ? 1 : 0)}");
                rdpContent.AppendLine($"redirectdrives:i:{(ChkDrives.IsChecked == true ? 1 : 0)}");
                rdpContent.AppendLine($"redirectprinters:i:{(ChkPrinters.IsChecked == true ? 1 : 0)}");
                rdpContent.AppendLine($"smart sizing:i:{(ChkSmartSizing.IsChecked == true ? 1 : 0)}");
                rdpContent.AppendLine("dynamic resolution:i:1"); // 开启窗口拉伸动态刷新

                File.WriteAllText(tempRdpPath, rdpContent.ToString(), System.Text.Encoding.UTF8);

                Logger.LogInfo($"已将 RDP 配置文件写入: {tempRdpPath}");
                Logger.LogInfo($"正在唤醒外部 Windows mstsc.exe 远程桌面控制会话...");

                var mstscPsi = new ProcessStartInfo("mstsc.exe")
                {
                    UseShellExecute = false,
                    Arguments = $"\"{tempRdpPath}\""
                };
                var newProc = Process.Start(mstscPsi);
                if (newProc != null)
                {
                    lock (_rdpProcesses)
                    {
                        _rdpProcesses.Add(new RdpSessionProcess
                        {
                            Username = username,
                            Process = newProc,
                            IsHidden = false
                        });
                    }

                    // 保存手动输入的密码到本地凭据
                    try
                    {
                        CredentialHelper.SaveCredential($"RDPManager:{username}", username, password);
                        Logger.LogInfo($"已将用户 '{username}' 的密码自动保存至本地缓存凭证。");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"自动保存用户凭据失败: {ex.Message}");
                    }
                }

                // 延迟 10 秒清理临时 RDP 配置文件
                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(10000);
                    try
                    {
                        if (File.Exists(tempRdpPath))
                        {
                            File.Delete(tempRdpPath);
                            Logger.LogInfo($"已自动清理临时 RDP 配置文件。");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"清理临时 RDP 配置文件阻碍: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("拉起外部 mstsc 客户端失败", ex);
                MessageBox.Show($"远程桌面连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LaunchRdpConnection(string username, string password)
        {
            LaunchRdpConnection(username, password, false);
        }

        private void BtnCleanZombieSessions_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Logger.LogInfo("用户触发一键注销所有断开会话...");
            System.Threading.Tasks.Task.Run(() =>
            {
                TermWrapDeployer.CleanupDisconnectedSessions();
                Dispatcher.Invoke(() => RefreshSessions());
            });
        }

        private void BtnAddVirtualScreen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string username)
            {
                Logger.LogInfo($"用户选择强制为账户 '{username}' 新建一个虚拟桌面会话...");
                if (CredentialHelper.GetCredential($"RDPManager:{username}", out _, out string savedPwd))
                {
                    LaunchRdpConnection(username, savedPwd, forceNew: true);
                }
                else
                {
                    MessageBox.Show($"未找到本地账户 '{username}' 的密码凭证，请先在系统账号设置中选中该账户并输入密码以进行缓存，然后再试。", "未缓存密码", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void BtnToggleAdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            if (PanelAdvancedSettings.Visibility == Visibility.Collapsed)
            {
                PanelAdvancedSettings.Visibility = Visibility.Visible;
                TxtAdvancedSettingsArrow.Text = "▲";
            }
            else
            {
                PanelAdvancedSettings.Visibility = Visibility.Collapsed;
                TxtAdvancedSettingsArrow.Text = "▼";
            }
        }

        // ======================= 系统托盘及窗口关闭 =======================

        private void InitializeNotifyIcon()
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon();
                
                // 优先使用当前 EXE 嵌入的 icon.ico 图标
                try
                {
                    string? exePath = System.Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                    {
                        _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    }
                    else
                    {
                        _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                    }
                }
                catch
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }

                _notifyIcon.Text = "LocalRDP - 本地多用户虚拟桌面";
                _notifyIcon.Visible = true;
                _notifyIcon.DoubleClick += (s, args) => ShowMainWindow();

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                contextMenu.Items.Add("显示主窗口", null, (s, args) => ShowMainWindow());
                contextMenu.Items.Add("安全退出", null, (s, args) => ExitApplication());
                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex)
            {
                Logger.LogError("系统托盘注册异常", ex);
            }
        }

        private void ShowMainWindow()
        {
            Dispatcher.Invoke(() =>
            {
                this.Show();
                if (this.WindowState == WindowState.Minimized)
                {
                    this.WindowState = WindowState.Normal;
                }
                this.Activate();
            });
        }

        private void ExitApplication()
        {
            _isExplicitExit = true;
            _notifyIcon?.Dispose();

            // 关闭程序时注销睡眠控制状态
            SetThreadExecutionState(ES_CONTINUOUS);

            Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isExplicitExit)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true; // 拦截窗口彻底关闭，改为隐藏至后台托盘
            this.Hide();
            try
            {
                _notifyIcon?.ShowBalloonTip(2000, "LocalRDP 后台保活中", "程序已最小化至系统托盘，双击托盘图标可重新打开控制台。", System.Windows.Forms.ToolTipIcon.Info);
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            _notifyIcon?.Dispose();
            SetThreadExecutionState(ES_CONTINUOUS);
            base.OnClosed(e);
        }

        private void ComboTheme_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboTheme.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                if (Enum.TryParse(item.Tag?.ToString(), out ThemeMode mode))
                {
                    ThemeManager.SaveThemeMode(mode);
                }
            }
        }
    }

    // 会话数据绑定实体
    public class SessionItem
    {
        public int SessionId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string StateText { get; set; } = string.Empty;
        public string DurationText { get; set; } = string.Empty;
        public bool IsConsole { get; set; }
        public bool IsCurrentUser { get; set; }
    }
}
