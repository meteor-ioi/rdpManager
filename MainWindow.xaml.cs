using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
        private ObservableCollection<ConnectionItem> _connections = new ObservableCollection<ConnectionItem>();
        private DispatcherTimer _thumbnailTimer;
        private TabItem _dashboardTab;

        public MainWindow()
        {
            InitializeComponent();

            // 绑定连接列表数据源
            ConnectionCardsControl.ItemsSource = _connections;

            // 初始化固定的第一个 TabItem: 仪表盘
            _dashboardTab = new TabItem
            {
                Header = "仪表盘"
            };
            WorkspaceTabs.Items.Add(_dashboardTab);
            WorkspaceTabs.SelectedIndex = 0;

            // 启动定时器，每 3 秒刷新一次连接网格的屏幕截图
            _thumbnailTimer = new DispatcherTimer();
            _thumbnailTimer.Interval = TimeSpan.FromSeconds(3);
            _thumbnailTimer.Tick += ThumbnailTimer_Tick;
            _thumbnailTimer.Start();

            // 检测 TermWrap 补丁状态
            RefreshTermWrapStatus();

            // 加载本地账户列表
            LoadAccounts();

            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 默认展示仪表盘
            SwitchToView(ViewWorkspaces);
            UpdateNavButtons(NavDashboardBtn);
        }

        // ======================= 状态与数据加载 =======================

        private void RefreshTermWrapStatus()
        {
            bool isActive = TermWrapDeployer.IsMultiSessionActive();
            if (isActive)
            {
                StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString("#0070F3"); // Vercel 经典蓝色
                StatusTxt.Text = "并发会话已激活 (TermWrap)";
                TermWrapStatusTxt.Text = "已激活 (服务已劫持)";
                TermWrapStatusTxt.Foreground = (Brush)new BrushConverter().ConvertFromString("#0070F3");
            }
            else
            {
                StatusDot.Fill = Brushes.Red;
                StatusTxt.Text = "并发会话未激活 / 补丁失效";
                TermWrapStatusTxt.Text = "未激活 / 默认单会话模式";
                TermWrapStatusTxt.Foreground = Brushes.Red;
            }
        }

        private void LoadAccounts()
        {
            var accounts = AccountHelper.GetLocalAccounts();
            AccountsDataGrid.ItemsSource = accounts;
        }

        // ======================= 侧边栏导航切换 =======================

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button clickedBtn)
            {
                UpdateNavButtons(clickedBtn);

                if (clickedBtn == NavDashboardBtn)
                {
                    SwitchToView(ViewWorkspaces);
                    WorkspaceTabs.SelectedIndex = 0; // 选定仪表盘
                }
                else if (clickedBtn == NavAccountsBtn)
                {
                    SwitchToView(ViewAccounts);
                    LoadAccounts();
                }
                else if (clickedBtn == NavSettingsBtn)
                {
                    SwitchToView(ViewSettings);
                    RefreshTermWrapStatus();
                }
            }
        }

        private void SwitchToView(Grid activeView)
        {
            ViewWorkspaces.Visibility = (activeView == ViewWorkspaces) ? Visibility.Visible : Visibility.Collapsed;
            ViewAccounts.Visibility = (activeView == ViewAccounts) ? Visibility.Visible : Visibility.Collapsed;
            ViewSettings.Visibility = (activeView == ViewSettings) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateNavButtons(Button activeBtn)
        {
            Style activeStyle = (Style)FindResource("ActiveSidebarBtnStyle");
            Style normalStyle = (Style)FindResource("SidebarBtnStyle");

            NavDashboardBtn.Style = (activeBtn == NavDashboardBtn) ? activeStyle : normalStyle;
            NavAccountsBtn.Style = (activeBtn == NavAccountsBtn) ? activeStyle : normalStyle;
            NavSettingsBtn.Style = (activeBtn == NavSettingsBtn) ? activeStyle : normalStyle;
        }

        // ======================= 标签页管理与切换 =======================

        private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WorkspaceTabs.SelectedIndex == 0)
            {
                // 显示仪表盘
                DashboardGridView.Visibility = Visibility.Visible;

                // 虚无化后台容器以切换展示，但由于仍保留在视觉树，后台依然能够保持渲染与截图
                ActiveRdpContainer.Opacity = 0;
                ActiveRdpContainer.IsHitTestVisible = false;
                ActiveRdpContainer.Visibility = Visibility.Visible;
            }
            else if (WorkspaceTabs.SelectedIndex > 0)
            {
                // 隐藏仪表盘，显示会话页面
                DashboardGridView.Visibility = Visibility.Collapsed;
                ActiveRdpContainer.Opacity = 1;
                ActiveRdpContainer.IsHitTestVisible = true;
                ActiveRdpContainer.Visibility = Visibility.Visible;

                // 激活对应的 RDP 控制实例，将其余保活会话隐形
                if (WorkspaceTabs.SelectedItem is TabItem selectedTab && selectedTab.Tag is ConnectionItem connItem)
                {
                    foreach (var item in _connections)
                    {
                        if (item.RdpControl != null)
                        {
                            if (item == connItem)
                            {
                                item.RdpControl.IsHiddenSession = false;
                            }
                            else
                            {
                                item.RdpControl.IsHiddenSession = true;
                            }
                        }
                    }
                }
            }
        }

        private void CloseTabToKeepAlive(ConnectionItem connItem)
        {
            if (connItem.RdpControl != null && connItem.RdpControl.IsConnected)
            {
                // 虚假断开：将 RDP 控件的透明度调为 0，允许其在后台维持渲染
                connItem.RdpControl.IsHiddenSession = true;
                connItem.StatusText = "后台保活";
                connItem.StatusBrush = Brushes.Green; // 后台运行时状态为绿色
            }

            // 从 Tab 列表中移除对应标签页
            TabItem? tabToRemove = null;
            for (int i = 1; i < WorkspaceTabs.Items.Count; i++)
            {
                if (WorkspaceTabs.Items[i] is TabItem t && t.Tag == connItem)
                {
                    tabToRemove = t;
                    break;
                }
            }

            if (tabToRemove != null)
            {
                WorkspaceTabs.Items.Remove(tabToRemove);
            }

            // 切换回仪表盘
            WorkspaceTabs.SelectedIndex = 0;
        }

        // ======================= 定时器与卡片截图 =======================

        private void ThumbnailTimer_Tick(object? sender, EventArgs e)
        {
            foreach (var conn in _connections)
            {
                if (conn.RdpControl != null && conn.RdpControl.IsConnected)
                {
                    var thumb = conn.RdpControl.CaptureThumbnail();
                    if (thumb != null)
                    {
                        conn.Thumbnail = thumb;
                        conn.PlaceholderVisibility = Visibility.Collapsed;
                    }
                }
            }
        }

        // ======================= 卡片按钮操作事件 =======================

        private void OpenSessionTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string connId)
            {
                var connItem = _connections.FirstOrDefault(c => c.Id == connId);
                if (connItem == null) return;

                // 切换回主视图
                SwitchToView(ViewWorkspaces);
                UpdateNavButtons(NavDashboardBtn);

                // 检查是否已存在 Tab 页
                TabItem? existingTab = null;
                for (int i = 1; i < WorkspaceTabs.Items.Count; i++)
                {
                    if (WorkspaceTabs.Items[i] is TabItem t && t.Tag == connItem)
                    {
                        existingTab = t;
                        break;
                    }
                }

                if (existingTab != null)
                {
                    WorkspaceTabs.SelectedItem = existingTab;
                }
                else
                {
                    // 控件未初始化
                    if (connItem.RdpControl == null)
                    {
                        var rdpCtrl = new RdpClientControl();
                        connItem.RdpControl = rdpCtrl;

                        // 将控件塞入常驻容器
                        ActiveRdpContainer.Children.Add(rdpCtrl);

                        connItem.StatusText = "正在连接...";
                        connItem.StatusBrush = Brushes.Orange;

                        // 绑定事件
                        rdpCtrl.OnRdpConnected += (s, ev) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                connItem.StatusText = "已连接";
                                connItem.StatusBrush = (Brush)new BrushConverter().ConvertFromString("#0070F3");
                                connItem.ActiveActionsVisibility = Visibility.Visible;
                                connItem.PlaceholderVisibility = Visibility.Collapsed;
                                connItem.Thumbnail = rdpCtrl.CaptureThumbnail();
                            });
                        };

                        rdpCtrl.OnRdpDisconnected += (s, reason) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                connItem.StatusText = "已断开";
                                connItem.StatusBrush = Brushes.Red;
                                connItem.ActiveActionsVisibility = Visibility.Collapsed;
                                connItem.PlaceholderVisibility = Visibility.Visible;
                                connItem.Thumbnail = null;

                                if (connItem.RdpControl != null)
                                {
                                    ActiveRdpContainer.Children.Remove(connItem.RdpControl);
                                    connItem.RdpControl = null;
                                }

                                TabItem? tabToRemove = null;
                                for (int i = 1; i < WorkspaceTabs.Items.Count; i++)
                                {
                                    if (WorkspaceTabs.Items[i] is TabItem t && t.Tag == connItem)
                                    {
                                        tabToRemove = t;
                                        break;
                                    }
                                }
                                if (tabToRemove != null)
                                {
                                    WorkspaceTabs.Items.Remove(tabToRemove);
                                }

                                MessageBox.Show($"会话 '{connItem.FriendlyName}' 发生意外断开: {reason}", "会话断开", MessageBoxButton.OK, MessageBoxImage.Warning);
                            });
                        };

                        // 触发 RDP 连接
                        bool enableUsb = OptUsbChk.IsChecked == true;
                        rdpCtrl.Connect(connItem.Server, connItem.Username, connItem.Password, enableUsb);
                    }

                    // 新建 TabPage
                    var newTab = new TabItem { Tag = connItem };

                    // 自定义带关闭按钮的标签栏 Header
                    var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    headerPanel.Children.Add(new TextBlock { Text = connItem.FriendlyName, VerticalAlignment = VerticalAlignment.Center });

                    var closeBtn = new Button
                    {
                        Content = "×",
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.Gray,
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Margin = new Thickness(8, 0, 0, 0),
                        Padding = new Thickness(4, 0, 4, 0),
                        Cursor = Cursors.Hand
                    };
                    closeBtn.Click += (s, ev) =>
                    {
                        ev.Handled = true;
                        CloseTabToKeepAlive(connItem);
                    };
                    headerPanel.Children.Add(closeBtn);

                    newTab.Header = headerPanel;
                    WorkspaceTabs.Items.Add(newTab);
                    WorkspaceTabs.SelectedItem = newTab;
                }
            }
        }

        private void KeepAliveDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string connId)
            {
                var connItem = _connections.FirstOrDefault(c => c.Id == connId);
                if (connItem == null) return;

                bool isTabOpen = false;
                for (int i = 1; i < WorkspaceTabs.Items.Count; i++)
                {
                    if (WorkspaceTabs.Items[i] is TabItem t && t.Tag == connItem)
                    {
                        isTabOpen = true;
                        break;
                    }
                }

                if (isTabOpen)
                {
                    // 若处于激活显示的标签页中，点击“断开并保活”即将该标签页关闭，置于后台静音渲染
                    CloseTabToKeepAlive(connItem);
                }
                else
                {
                    // 若已处于后台保活状态下，点击则表明“彻底注销”断开连接并清理句柄
                    var result = MessageBox.Show($"是否确定彻底断开会话 '{connItem.FriendlyName}'？这将清理该隔离账户下的所有执行进程。", "断开连接", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        connItem.RdpControl?.Disconnect();
                    }
                }
            }
        }

        // ======================= 新建连接弹窗 =======================

        private void OpenNewConnection_Click(object sender, RoutedEventArgs e)
        {
            TargetPasswordBox.Password = string.Empty;
            FriendlyNameTxt.Text = string.Empty;

            TargetComputerCombo.Items.Clear();
            TargetComputerCombo.Items.Add("127.0.0.2");

            // 读取已创建的隔离账户以便快速选择
            var localAccounts = AccountHelper.GetLocalAccounts();
            foreach (var acc in localAccounts)
            {
                TargetComputerCombo.Items.Add(acc.Name);
            }

            if (TargetComputerCombo.Items.Count > 0)
            {
                TargetComputerCombo.SelectedIndex = 0;
            }

            NewConnectionOverlay.Visibility = Visibility.Visible;
        }

        private void CloseNewConnection_Click(object sender, RoutedEventArgs e)
        {
            NewConnectionOverlay.Visibility = Visibility.Collapsed;
        }

        private void AddConnectionSubmit_Click(object sender, RoutedEventArgs e)
        {
            string targetText = TargetComputerCombo.Text.Trim();
            string password = TargetPasswordBox.Password;
            string friendlyName = FriendlyNameTxt.Text.Trim();

            if (string.IsNullOrEmpty(targetText))
            {
                MessageBox.Show("请指定目标主机或选择本地隔离账户。");
                return;
            }

            string server = targetText;
            string username = string.Empty;

            if (targetText.Contains("\\"))
            {
                string[] parts = targetText.Split('\\');
                server = parts[0];
                username = parts[1];
            }
            else if (!targetText.Contains(".") && !targetText.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                // 本地多会话连接本机首选 loopback 地址 127.0.0.2，避免单路 IP 拦截
                server = "127.0.0.2";
                username = targetText;
            }
            else
            {
                MessageBox.Show("非法连接信息，请按照以下格式之一输入:\n• 本地账户名（如: RpaUser_1）\n• 远程目标: IP\\用户名（如: 192.168.1.10\\Administrator）", "格式错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 密码读取逻辑（如果在界面没有输入，则尝试去 Credential Manager 获取）
            if (string.IsNullOrEmpty(password))
            {
                if (CredentialHelper.GetCredential($"RDPManager:{username}", out _, out string savedPwd))
                {
                    password = savedPwd;
                }
                else
                {
                    MessageBox.Show($"未找到本地账户 '{username}' 的保存密码，请在上方手动输入。", "请输入密码");
                    return;
                }
            }
            else
            {
                // 输入了密码，对其进行持久化更新
                CredentialHelper.SaveCredential($"RDPManager:{username}", username, password);
            }

            if (string.IsNullOrEmpty(friendlyName))
            {
                friendlyName = $"{username} ({server})";
            }

            var newItem = new ConnectionItem
            {
                Id = Guid.NewGuid().ToString(),
                FriendlyName = friendlyName,
                Server = server,
                Username = username,
                Password = password
            };

            _connections.Add(newItem);
            NewConnectionOverlay.Visibility = Visibility.Collapsed;
        }

        // ======================= 隔离账户管理 =======================

        private void CreateAccount_Click(object sender, RoutedEventArgs e)
        {
            string username = NewUserTxt.Text.Trim();
            string password = NewPassTxt.Password;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("用户名和密码不能为空。");
                return;
            }

            if (AccountHelper.CreateRobotAccount(username, password, out string error))
            {
                // 创建成功后同时将密码注册至安全凭据
                CredentialHelper.SaveCredential($"RDPManager:{username}", username, password);
                MessageBox.Show($"隔离账号 '{username}' 已创建成功，自动完成管理员特权分配与环境首登优化！");
                NewUserTxt.Text = string.Empty;
                NewPassTxt.Password = string.Empty;
                LoadAccounts();
            }
            else
            {
                MessageBox.Show($"账户创建失败: {error}");
            }
        }

        private void DeleteAccount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string username)
            {
                var result = MessageBox.Show($"警告：您即将删除本地系统账户 '{username}'。删除后该账户所有会话、数据、桌面缓存均将被清空！是否继续？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Stop);
                if (result == MessageBoxResult.Yes)
                {
                    if (AccountHelper.DeleteRobotAccount(username, out string error))
                    {
                        MessageBox.Show($"账户 '{username}' 已成功删除。");
                        LoadAccounts();
                    }
                    else
                    {
                        MessageBox.Show($"删除失败: {error}");
                    }
                }
            }
        }

        // ======================= 系统设置选项 =======================

        private void DeployPatch_Click(object sender, RoutedEventArgs e)
        {
            DeployPatchBtn.IsEnabled = false;
            try
            {
                if (TermWrapDeployer.DeployPatch(out string error))
                {
                    MessageBox.Show("TermWrap 多路并发 RDP 补丁部署成功！\n系统已解除多路限制，外设摄像头重定向等底层策略已激活。", "激活成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"部署失败: {error}\n请确认已排除安全软件拦截，或重启电脑后再试。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                DeployPatchBtn.IsEnabled = true;
                RefreshTermWrapStatus();
            }
        }

        private void UninstallPatch_Click(object sender, RoutedEventArgs e)
        {
            UninstallPatchBtn.IsEnabled = false;
            try
            {
                var result = MessageBox.Show("还原补丁将使 Windows 远程桌面多会话并发功能恢复为系统原始出厂配置。是否继续？", "确认还原", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    if (TermWrapDeployer.UninstallPatch(out string error))
                    {
                        if (string.IsNullOrEmpty(error))
                        {
                            MessageBox.Show("TermWrap 补丁卸载成功，远程桌面控制服务已恢复出厂配置。", "卸载成功");
                        }
                        else
                        {
                            MessageBox.Show(error, "卸载提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else
                    {
                        MessageBox.Show($"卸载失败: {error}", "还原错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            finally
            {
                UninstallPatchBtn.IsEnabled = true;
                RefreshTermWrapStatus();
            }
        }
    }

    // ======================= 连接项数据实体 =======================

    public class ConnectionItem : INotifyPropertyChanged
    {
        private string _statusText = "未连接";
        private Brush _statusBrush = Brushes.Gray;
        private BitmapSource? _thumbnail;
        private Visibility _placeholderVisibility = Visibility.Visible;
        private Visibility _activeActionsVisibility = Visibility.Collapsed;

        public string Id { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public RdpClientControl? RdpControl { get; set; }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public Brush StatusBrush
        {
            get => _statusBrush;
            set { _statusBrush = value; OnPropertyChanged(); }
        }

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); }
        }

        public Visibility PlaceholderVisibility
        {
            get => _placeholderVisibility;
            set { _placeholderVisibility = value; OnPropertyChanged(); }
        }

        public Visibility ActiveActionsVisibility
        {
            get => _activeActionsVisibility;
            set { _activeActionsVisibility = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
