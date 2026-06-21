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
        private string _currentPassword = string.Empty;

        // 系统托盘相关
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isExplicitExit = false;

        // 阻止睡眠 API
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        public MainWindow()
        {
            InitializeComponent();

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
                AdminTag.Background = (Brush)new BrushConverter().ConvertFromString("#ECFDF5")!; // 浅绿
                AdminTagTxt.Text = "已提权";
                AdminTagTxt.Foreground = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
                PrivilegeWarningAlert.Visibility = Visibility.Collapsed;
            }
            else
            {
                AdminTag.Background = (Brush)new BrushConverter().ConvertFromString("#FEF2F2")!; // 浅红
                AdminTagTxt.Text = "受限运行";
                AdminTagTxt.Foreground = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
                PrivilegeWarningAlert.Visibility = Visibility.Visible;
            }

            // 2. 判断 RDP 服务状态
            bool isRdpRunning = TermWrapDeployer.IsTermServiceRunning();
            if (isRdpRunning)
            {
                RdpTag.Background = (Brush)new BrushConverter().ConvertFromString("#ECFDF5")!;
                RdpTagTxt.Text = "RDP开";
                RdpTagTxt.Foreground = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
            }
            else
            {
                RdpTag.Background = (Brush)new BrushConverter().ConvertFromString("#F4F4F5")!;
                RdpTagTxt.Text = "RDP关";
                RdpTagTxt.Foreground = (Brush)new BrushConverter().ConvertFromString("#71717A")!;
            }

            // 3. 判断补丁激活状态
            bool isTermWrapActive = TermWrapDeployer.IsMultiSessionActive();
            if (isTermWrapActive)
            {
                TermWrapStatusLabel.Text = "已启用";
                TermWrapStatusLabel.Foreground = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
            }
            else
            {
                TermWrapStatusLabel.Text = "未安装";
                TermWrapStatusLabel.Foreground = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
            }

            bool isEndpWrapActive = TermWrapDeployer.IsEndpWrapActive();
            if (isEndpWrapActive)
            {
                EndpWrapStatusLabel.Text = "已启用";
                EndpWrapStatusLabel.Foreground = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
            }
            else
            {
                EndpWrapStatusLabel.Text = "未安装";
                EndpWrapStatusLabel.Foreground = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
            }
        }

        private async void BtnInstallPatch_Click(object sender, RoutedEventArgs e)
        {
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
                return;
            }

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
            NewUserModal.Visibility = Visibility.Visible;
        }

        private void BtnCancelNewUser_Click(object sender, RoutedEventArgs e)
        {
            NewUserModal.Visibility = Visibility.Collapsed;
        }

        private void BtnGeneratePassword_Click(object sender, RoutedEventArgs e)
        {
            NewUserPasswordTxt.Text = GenerateStrongPassword();
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
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(3000);

                        foreach (string line in output.Split('\n'))
                        {
                            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("SESSIONNAME", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (line.Contains("console", StringComparison.OrdinalIgnoreCase) || line.Contains("services", StringComparison.OrdinalIgnoreCase))
                                continue;

                            bool isActive = line.Contains("Active", StringComparison.OrdinalIgnoreCase);
                            bool isDisc = line.Contains("Disc", StringComparison.OrdinalIgnoreCase);

                            if (!isActive && !isDisc)
                                continue;

                            if (line.Length >= 23)
                            {
                                string idStr = line.Substring(19, Math.Min(5, line.Length - 19)).Trim();
                                if (int.TryParse(idStr, out int sessionId))
                                {
                                    // 提取用户名
                                    string userSection = line.Length > 35 ? line.Substring(10, 9).Trim() : "";
                                    if (string.IsNullOrEmpty(userSection))
                                    {
                                        // 尝试使用多空格分隔符解析
                                        string[] tokens = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                        if (tokens.Length > 1)
                                        {
                                            userSection = tokens[0].StartsWith(">") ? tokens[1] : tokens[0];
                                            if (int.TryParse(userSection, out _)) userSection = "RdpUser";
                                        }
                                    }

                                    sessions.Add(new SessionItem
                                    {
                                        SessionId = sessionId,
                                        Username = string.IsNullOrEmpty(userSection) ? "RDP回环会话" : userSection,
                                        StateText = isActive ? "🟢 活跃" : "🟡 断开",
                                        DurationText = "保活中"
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
                Logger.LogInfo($"正在尝试将系统 RDP 会话 {sessionId} 挂起至后台...");
                try
                {
                    var psi = new ProcessStartInfo("tsdiscon", sessionId.ToString())
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var proc = Process.Start(psi))
                    {
                        proc?.WaitForExit(3000);
                    }
                    Logger.LogInfo($"已发送 RDP 会话 {sessionId} 挂起指令。");
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

        private void LaunchRdpConnection(string username, string password)
        {
            string server = "127.0.0.2"; // 并发连接本机回环 IP
            
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
                string tempRdpPath = Path.Combine(Path.GetTempPath(), $"rdp_{Guid.NewGuid():N}.rdp");
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
                Process.Start(mstscPsi);

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
    }

    // 会话数据绑定实体
    public class SessionItem
    {
        public int SessionId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string StateText { get; set; } = string.Empty;
        public string DurationText { get; set; } = string.Empty;
    }
}
