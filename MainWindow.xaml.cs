using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rdpManager.Helpers;
using rdpManager.Views;

namespace rdpManager
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<WtsSessionInfo> _sessions = new ObservableCollection<WtsSessionInfo>();
        private readonly HashSet<string> _keepAliveUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RdpWindow> _activeWindows = new Dictionary<string, RdpWindow>(StringComparer.OrdinalIgnoreCase);
        
        private DispatcherTimer? _pollTimer;
        private CancellationTokenSource? _guardCts;
        private Task? _guardTask;
        
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isExplicitExit = false;

        public MainWindow()
        {
            InitializeComponent();
            
            // 数据源绑定
            SessionsListView.ItemsSource = _sessions;

            // 检查管理员身份
            CheckAdminPrivileges();

            // 初始化系统托盘
            InitializeNotifyIcon();

            this.Loaded += MainWindow_Loaded;
            this.Closed += MainWindow_Closed;
        }

        private void CheckAdminPrivileges()
        {
            try
            {
                bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                AdminWarningBanner.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex)
            {
                Logger.LogError("检查管理员权限失败", ex);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 刷新补丁状态
            RefreshTermWrapStatus();

            // 加载本地账号
            LoadAccountsAsync();

            // 启动 WTS 会话轮询定时器 (4 秒一次)
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();

            // 立即进行一次会话拉取
            PollTimer_Tick(null, EventArgs.Empty);

            // 启动保活守护线程
            _guardCts = new CancellationTokenSource();
            _guardTask = Task.Run(() => GuardLoop(_guardCts.Token));

            Logger.LogInfo("主窗口加载完成，会话轮询与保活守护 Task 已启动。");
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _pollTimer?.Stop();
            
            if (_guardCts != null)
            {
                _guardCts.Cancel();
                _guardCts.Dispose();
            }

            _notifyIcon?.Dispose();
        }

        // ======================= WTS 会话轮询与更新 =======================

        private void PollTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                // 拉取最新 WTS 会话列表
                var currentSessions = WtsHelper.GetWtsSessions();

                // 1. 删除在新列表中不存在的旧会话
                for (int i = _sessions.Count - 1; i >= 0; i--)
                {
                    var oldSession = _sessions[i];
                    if (!currentSessions.Any(s => s.SessionId == oldSession.SessionId))
                    {
                        _sessions.RemoveAt(i);
                    }
                }

                // 2. 添加或差分更新会话
                foreach (var newSession in currentSessions)
                {
                    var existing = _sessions.FirstOrDefault(s => s.SessionId == newSession.SessionId);
                    if (existing == null)
                    {
                        _sessions.Add(newSession);
                    }
                    else
                    {
                        existing.UserName = newSession.UserName;
                        existing.State = newSession.State;
                        existing.ClientWidth = newSession.ClientWidth;
                        existing.ClientHeight = newSession.ClientHeight;
                        existing.RunningTime = newSession.RunningTime;
                    }
                }

                // 3. 更新 UI 指示器
                SessionCountBadge.Text = _sessions.Count.ToString();
                NoSessionsTip.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.LogError("轮询 WTS 会话异常", ex);
            }
        }

        // ======================= 核心保活与阻止睡眠守护 =======================

        private async Task GuardLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 阻止系统锁屏/休眠
                    bool preventSleep = false;
                    Dispatcher.Invoke(() => preventSleep = PreventSleepChk.IsChecked == true);

                    if (preventSleep)
                    {
                        WtsHelper.PreventSleep();
                    }
                    else
                    {
                        WtsHelper.AllowSleep();
                    }

                    // 检查保活分辨率锁定开关
                    bool lockRes = false;
                    Dispatcher.Invoke(() => lockRes = LockResolutionChk.IsChecked == true);

                    // 查询所有当前会话
                    var currentSessions = WtsHelper.GetWtsSessions();

                    // 对已保活列表中的用户进行扫描
                    string[] targetUsers;
                    lock (_keepAliveUsers)
                    {
                        targetUsers = _keepAliveUsers.ToArray();
                    }

                    foreach (var username in targetUsers)
                    {
                        var session = currentSessions.FirstOrDefault(s => s.UserName.Equals(username, StringComparison.OrdinalIgnoreCase));
                        if (session != null)
                        {
                            // 如果会话在系统中且状态为断开 (Disconnected)
                            if (session.State == WtsConnectState.Disconnected)
                            {
                                Logger.LogInfo($"[Guard] 检测到保活用户 '{session.UserName}' 的会话处于 Disconnected 状态 (SessionId={session.SessionId})，发起控制台劫持...");
                                
                                // 执行 tscon 回物理控制台
                                bool success = WtsHelper.TsconToConsole(session.SessionId);
                                if (success)
                                {
                                    Logger.LogInfo($"[Guard] 会话 '{session.UserName}' tscon 劫持成功！");

                                    // 如果勾选锁定分辨率，强行同步分辨率
                                    if (lockRes && session.ClientWidth > 0 && session.ClientHeight > 0)
                                    {
                                        bool resOk = WtsHelper.LockResolution(session.ClientWidth, session.ClientHeight);
                                        Logger.LogInfo($"[Guard] 强制物理显示分辨率同步为 {session.ClientWidth}x{session.ClientHeight}: {(resOk ? "成功" : "失败")}");
                                    }
                                }
                                else
                                {
                                    Logger.LogWarning($"[Guard] 会话 '{session.UserName}' tscon 劫持失败。");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("GuardLoop 循环迭代出现异常", ex);
                }

                await Task.Delay(2000, token);
            }
        }

        // ======================= 隔离账号管理 =======================

        private async void LoadAccountsAsync(bool forceRefresh = false)
        {
            ShowLoading("正在拉取本地隔离账户列表...");
            try
            {
                var accounts = await Task.Run(() => AccountHelper.GetLocalAccounts(forceRefresh));
                AccountCombo.ItemsSource = accounts;
                if (accounts != null && accounts.Count > 0)
                {
                    AccountCombo.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("加载本地账户失败", ex);
                MessageBox.Show($"加载账户失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideLoading();
            }
        }

        private void AccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AccountCombo.SelectedItem is string username)
            {
                // 自动载入已保存凭证
                if (CredentialHelper.GetCredential($"RDPManager:{username}", out _, out string pwd))
                {
                    SelectedAccountPasswordTxt.Password = pwd;
                }
                else
                {
                    SelectedAccountPasswordTxt.Password = string.Empty;
                }
            }
        }

        private void SelectedAccountPasswordTxt_PasswordChanged(object sender, RoutedEventArgs e)
        {
            // 用户输入时临时更新，点击保存密码或者直接连接时会保存
        }

        private void SaveCredBtn_Click(object sender, RoutedEventArgs e)
        {
            if (AccountCombo.SelectedItem is string username)
            {
                string pwd = SelectedAccountPasswordTxt.Password;
                if (CredentialHelper.SaveCredential($"RDPManager:{username}", username, pwd))
                {
                    MessageBox.Show($"账户 '{username}' 的凭证密码保存成功！下次登录将自动读取填充。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("密码保存失败，请检查凭据管理器权限。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("请先在下拉框中选择一个系统账号！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void CreateAccount_Click(object sender, RoutedEventArgs e)
        {
            string username = NewUserTxt.Text.Trim();
            string password = NewPassTxt.Password;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("隔离用户账号与初始密码不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ShowLoading($"正在创建隔离账号 '{username}' 并配置环境...");
            bool success = false;
            string error = string.Empty;

            try
            {
                var result = await Task.Run(() =>
                {
                    bool createResult = AccountHelper.CreateRobotAccount(username, password, out string err);
                    if (createResult)
                    {
                        CredentialHelper.SaveCredential($"RDPManager:{username}", username, password);
                    }
                    return new { Success = createResult, Error = err };
                });

                success = result.Success;
                error = result.Error;
            }
            catch (Exception ex)
            {
                success = false;
                error = ex.Message;
                Logger.LogError($"创建隔离账号 '{username}' 异常", ex);
            }
            finally
            {
                HideLoading();
            }

            if (success)
            {
                MessageBox.Show($"账号 '{username}' 创建成功！自动完成管理员环境和首登组策略优化。", "创建成功", MessageBoxButton.OK, MessageBoxImage.Information);
                NewUserTxt.Text = string.Empty;
                NewPassTxt.Password = string.Empty;
                LoadAccountsAsync(true);
            }
            else
            {
                MessageBox.Show($"账户创建失败: {error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
        {
            if (AccountCombo.SelectedItem is string username)
            {
                var result = MessageBox.Show($"警告：您即将从 Windows 系统中删除账号 '{username}'。\n这会永久清空该用户的所有文件与桌面数据！是否确认删除？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Stop);
                if (result == MessageBoxResult.Yes)
                {
                    ShowLoading($"正在删除账户 '{username}'...");
                    bool success = false;
                    string error = string.Empty;

                    try
                    {
                        var deleteResult = await Task.Run(() =>
                        {
                            return AccountHelper.DeleteRobotAccount(username, out string err)
                                ? new { Success = true, Error = string.Empty }
                                : new { Success = false, Error = err };
                        });

                        success = deleteResult.Success;
                        error = deleteResult.Error;
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        error = ex.Message;
                        Logger.LogError($"删除账号 '{username}' 时出现异常", ex);
                    }
                    finally
                    {
                        HideLoading();
                    }

                    if (success)
                    {
                        MessageBox.Show($"系统账号 '{username}' 已彻底删除。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        LoadAccountsAsync(true);
                    }
                    else
                    {
                        MessageBox.Show($"删除失败: {error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show("请先选择需要删除的系统账号！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ======================= 并发补丁管理 (TermWrap) =======================

        private void RefreshTermWrapStatus()
        {
            try
            {
                bool isActive = TermWrapDeployer.IsMultiSessionActive();
                bool isRunning = TermWrapDeployer.IsTermServiceRunning();
                
                Logger.LogInfo($"检测 TermWrap 状态: {(isActive ? "已应用" : "未应用")}, 远程服务: {(isRunning ? "运行中" : "停止")}");
                
                if (isActive)
                {
                    if (isRunning)
                    {
                        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString("#4ADE80")!; // 绿色
                        StatusTxt.Text = "并发会话已激活 (多用户并发就绪)";
                        TermWrapStatusTxt.Text = "已激活 (服务运行中)";
                        TermWrapStatusTxt.Foreground = (Brush)new BrushConverter().ConvertFromString("#4ADE80")!;
                    }
                    else
                    {
                        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString("#FACC15")!; // 黄色
                        StatusTxt.Text = "补丁已激活，但远程桌面服务已停止";
                        TermWrapStatusTxt.Text = "已激活 (服务已停止)";
                        TermWrapStatusTxt.Foreground = (Brush)new BrushConverter().ConvertFromString("#FACC15")!;
                    }
                }
                else
                {
                    StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString("#E94560")!; // 玫红/红色
                    StatusTxt.Text = "并发限制未解除 / 仅单会话模式";
                    TermWrapStatusTxt.Text = "未安装 / 默认单会话限制";
                    TermWrapStatusTxt.Foreground = (Brush)new BrushConverter().ConvertFromString("#E94560")!;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("获取补丁状态异常", ex);
            }
        }

        private async void DeployPatch_Click(object sender, RoutedEventArgs e)
        {
            DeployPatchBtn.IsEnabled = false;
            ShowLoading("正在部署并应用 TermWrap 并发补丁，可能需要几秒钟时间，这会导致所有活跃的 RDP 会话临时断开...");
            bool success = false;
            string error = string.Empty;

            try
            {
                var result = await Task.Run(() =>
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
                Logger.LogError("执行 DeployPatch 发生异常", ex);
            }
            finally
            {
                HideLoading();
                DeployPatchBtn.IsEnabled = true;
                RefreshTermWrapStatus();
            }

            if (success)
            {
                MessageBox.Show("多路并发 RDP 补丁部署并激活成功！系统目前允许多账号同时远程登录桌面运行任务。", "激活成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"部署失败: {error}\n请检查防杀毒软件拦截，并确保以管理员特权运行此程序。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void UninstallPatch_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确定要卸载 TermWrap 补丁并还原系统远程服务配置么？\n这会让 Windows 远程服务退回到出厂的单会话限制状态。", "确认还原", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            UninstallPatchBtn.IsEnabled = false;
            ShowLoading("正在卸载补丁并重新启动远程桌面服务，请稍候...");
            bool success = false;
            string error = string.Empty;

            try
            {
                var runResult = await Task.Run(() =>
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
                Logger.LogError("执行 UninstallPatch 发生异常", ex);
            }
            finally
            {
                HideLoading();
                UninstallPatchBtn.IsEnabled = true;
                RefreshTermWrapStatus();
            }

            if (success)
            {
                MessageBox.Show("TermWrap 补丁已彻底卸载，系统已重新加载原始远程控制核心组件。", "卸载成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"卸载失败: {error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ======================= 发起 RDP 连接逻辑 =======================

        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!(AccountCombo.SelectedItem is string username))
            {
                MessageBox.Show("请先选择要发起远程连接的系统隔离账号！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string password = SelectedAccountPasswordTxt.Password;
            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show("请输入该系统账号的登录密码凭据，以便自动填写进行单机免密 RDP 隔离登录！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 保存一次凭证密码
            CredentialHelper.SaveCredential($"RDPManager:{username}", username, password);

            // 获取画面参数
            int width = 0;
            int height = 0;
            if (ResolutionCombo.SelectedItem is ComboBoxItem resItem && resItem.Tag is string resStr)
            {
                if (resStr != "0x0")
                {
                    var parts = resStr.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    {
                        width = w;
                        height = h;
                    }
                }
            }

            int scale = 100;
            if (ScaleCombo.SelectedItem is ComboBoxItem scaleItem && scaleItem.Tag is string scaleStr && int.TryParse(scaleStr, out int sValue))
            {
                scale = sValue;
            }

            bool clipboard = ClipboardChk.IsChecked == true;
            bool audio = AudioChk.IsChecked == true;
            bool mic = MicChk.IsChecked == true;
            bool drives = DrivesChk.IsChecked == true;
            bool printers = PrintersChk.IsChecked == true;
            bool smartSizing = SmartSizingChk.IsChecked == true;
            bool autoKeepAlive = AutoKeepAliveChk.IsChecked == true;

            StartRdpSession(username, password, width, height, scale, clipboard, audio, mic, drives, printers, smartSizing, autoKeepAlive);
        }

        private void StartRdpSession(string username, string password, 
            int width, int height, int scale, 
            bool clipboard, bool audio, bool mic, bool drives, bool printers, bool smartSizing, bool autoKeepAlive)
        {
            // 如果窗口已存在，则激活它
            if (_activeWindows.TryGetValue(username, out var activeWin))
            {
                activeWin.WindowState = WindowState.Normal;
                activeWin.Activate();
                return;
            }

            // 本地隔离 RDP 连接回送地址：使用 127.0.0.2 强制系统进行网络隔离环回以启用多会话
            string server = "127.0.0.2";

            var rdpWin = new RdpWindow(
                server: server,
                username: username,
                password: password,
                width: width,
                height: height,
                scaleFactor: scale,
                redirectClipboard: clipboard,
                redirectAudio: audio,
                redirectMic: mic,
                redirectDrives: drives,
                redirectPrinters: printers,
                smartSizing: smartSizing,
                autoKeepAlive: autoKeepAlive
            );

            // 维护保活用户集合
            lock (_keepAliveUsers)
            {
                if (autoKeepAlive)
                {
                    _keepAliveUsers.Add(username);
                }
                else
                {
                    _keepAliveUsers.Remove(username);
                }
            }

            _activeWindows[username] = rdpWin;
            rdpWin.WindowClosedEvent += (win) =>
            {
                _activeWindows.Remove(win.UserName);
            };

            rdpWin.Show();
        }

        // ======================= 活跃会话操作 =======================

        private void OpenSession_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is WtsSessionInfo session)
            {
                // 获取密码凭据
                if (CredentialHelper.GetCredential($"RDPManager:{session.UserName}", out _, out string password) && !string.IsNullOrEmpty(password))
                {
                    // 获取当前界面上的重定向配置来打开会话
                    int width = session.ClientWidth > 0 ? session.ClientWidth : 1920;
                    int height = session.ClientHeight > 0 ? session.ClientHeight : 1080;

                    int scale = 100;
                    if (ScaleCombo.SelectedItem is ComboBoxItem scaleItem && scaleItem.Tag is string scaleStr && int.TryParse(scaleStr, out int sValue))
                    {
                        scale = sValue;
                    }

                    StartRdpSession(
                        username: session.UserName,
                        password: password,
                        width: width,
                        height: height,
                        scale: scale,
                        clipboard: ClipboardChk.IsChecked == true,
                        audio: AudioChk.IsChecked == true,
                        mic: MicChk.IsChecked == true,
                        drives: DrivesChk.IsChecked == true,
                        printers: PrintersChk.IsChecked == true,
                        smartSizing: SmartSizingChk.IsChecked == true,
                        autoKeepAlive: AutoKeepAliveChk.IsChecked == true
                    );
                }
                else
                {
                    MessageBox.Show($"未找到本地隔离账号 '{session.UserName}' 的密码凭证，请先在上方 [系统隔离账号管理] 中输入该账号的密码并点击 [保存密码]。", "凭证缺失", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void DisconnectSession_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int sessionId)
            {
                string username = _sessions.FirstOrDefault(s => s.SessionId == sessionId)?.UserName ?? $"会话 {sessionId}";
                var confirm = MessageBox.Show($"确定断开账户 '{username}' 的远程会话吗？(会话程序将在后台挂起，保留当前桌面状态)", "断开确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm == MessageBoxResult.Yes)
                {
                    WtsHelper.DisconnectSession(sessionId);
                    PollTimer_Tick(null, EventArgs.Empty);
                }
            }
        }

        private void LogoffSessionContext_Click(object sender, RoutedEventArgs e)
        {
            if (SessionsListView.SelectedItem is WtsSessionInfo session)
            {
                var confirm = MessageBox.Show($"警告：强制注销账户 '{session.UserName}' 会直接强制终止该用户下的所有运行程序，并丢失未保存的桌面数据，确认继续？", "注销警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm == MessageBoxResult.Yes)
                {
                    WtsHelper.LogoffSession(session.SessionId);
                    PollTimer_Tick(null, EventArgs.Empty);
                }
            }
        }

        // ======================= UI 面板折叠交互 =======================

        private void ToggleModule_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement header && header.Tag is string targetName)
            {
                var content = this.FindName(targetName) as FrameworkElement;
                if (content != null)
                {
                    content.Visibility = content.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    
                    var arrowName = targetName.Replace("Content", "Arrow");
                    var arrow = this.FindName(arrowName) as TextBlock;
                    if (arrow != null)
                    {
                        arrow.Text = content.Visibility == Visibility.Visible ? "▲" : "▽";
                    }
                }
            }
        }

        // ======================= 系统托盘与关闭行为控制 =======================

        private void InitializeNotifyIcon()
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon();
                _notifyIcon.Icon = System.Drawing.SystemIcons.Shield; // 用系统原生盾牌图标代表安全/保活
                _notifyIcon.Text = "LocalRDP — 多用户 RPA 隔离管理器";
                _notifyIcon.Visible = true;
                _notifyIcon.DoubleClick += (s, args) => ShowMainWindow();

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                contextMenu.Items.Add("显示主界面", null, (s, args) => ShowMainWindow());
                contextMenu.Items.Add("退出程序", null, (s, args) => ExitApplication());
                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex)
            {
                Logger.LogError("初始化托盘图标失败", ex);
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

            // 主动释放所有活动窗口中的 RDP 物理连接
            foreach (var win in _activeWindows.Values.ToArray())
            {
                try
                {
                    win.Close();
                }
                catch { }
            }

            Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isExplicitExit)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true; // 拦截窗口的物理关闭，转换为询问

            var confirmWin = new CloseConfirmWindow
            {
                Owner = this
            };
            confirmWin.ShowDialog();

            if (confirmWin.Result == CloseConfirmResult.Exit)
            {
                ExitApplication();
            }
            else if (confirmWin.Result == CloseConfirmResult.Minimize)
            {
                this.Hide();
                try
                {
                    _notifyIcon?.ShowBalloonTip(3000, "LocalRDP 已最小化", "程序已安全缩入托盘中进行后台 RPA 会话监视与保活锁定。", System.Windows.Forms.ToolTipIcon.Info);
                }
                catch { }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _notifyIcon?.Dispose();
            base.OnClosed(e);
        }

        private void ShowLoading(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LoadingText.Text = message;
                GlobalLoadingOverlay.Visibility = Visibility.Visible;
            });
        }

        private void HideLoading()
        {
            Dispatcher.Invoke(() =>
            {
                GlobalLoadingOverlay.Visibility = Visibility.Collapsed;
            });
        }
    }
}
