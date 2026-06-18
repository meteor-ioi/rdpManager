using System;
using System.Windows;
using rdpManager.Helpers;

namespace rdpManager.Views
{
    public partial class RdpWindow : Window
    {
        private readonly string _server;
        private readonly string _username;
        private readonly string _password;
        private readonly int _width;
        private readonly int _height;
        private readonly int _scaleFactor;
        private readonly bool _redirectClipboard;
        private readonly bool _redirectAudio;
        private readonly bool _redirectMic;
        private readonly bool _redirectDrives;
        private readonly bool _redirectPrinters;
        private readonly bool _smartSizing;

        public string UserName => _username;
        public bool AutoKeepAlive { get; }

        public event Action<RdpWindow>? WindowClosedEvent;

        public RdpWindow(string server, string username, string password, 
            int width, int height, int scaleFactor, 
            bool redirectClipboard, bool redirectAudio, bool redirectMic, 
            bool redirectDrives, bool redirectPrinters, bool smartSizing, bool autoKeepAlive)
        {
            InitializeComponent();

            _server = server;
            _username = username;
            _password = password;
            _width = width;
            _height = height;
            _scaleFactor = scaleFactor;
            _redirectClipboard = redirectClipboard;
            _redirectAudio = redirectAudio;
            _redirectMic = redirectMic;
            _redirectDrives = redirectDrives;
            _redirectPrinters = redirectPrinters;
            _smartSizing = smartSizing;
            AutoKeepAlive = autoKeepAlive;

            Title = $"LocalRDP — 隔离会话: {_username}";

            this.Loaded += RdpWindow_Loaded;
            this.Closed += RdpWindow_Closed;

            // 监听断开连接，关闭窗口
            RdpCtrl.OnRdpDisconnected += RdpCtrl_OnRdpDisconnected;
        }

        private void RdpWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.LogInfo($"RdpWindow 加载完成，正在连接到 RDP 会话 (User={_username})...");
                
                // 注意：RdpClientControl 内部会检查 muteAudio。
                // 这里的 muteAudio 传入 !redirectAudio (如果用户不需要音频重定向，则静音播放)
                bool muteAudio = !_redirectAudio;

                RdpCtrl.Connect(
                    server: _server,
                    username: _username,
                    password: _password,
                    enableUsb: false, // 默认不开启 USB，只按需开启其他
                    enableSmartSizing: _smartSizing,
                    enableClipboard: _redirectClipboard,
                    muteAudio: muteAudio,
                    desktopWidth: _width,
                    desktopHeight: _height,
                    desktopScaleFactor: _scaleFactor,
                    redirectMic: _redirectMic,
                    redirectDrives: _redirectDrives,
                    redirectPrinters: _redirectPrinters
                );
            }
            catch (Exception ex)
            {
                Logger.LogError($"RdpWindow.Connect 出现异常", ex);
                MessageBox.Show($"启动会话连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
            }
        }

        private void RdpCtrl_OnRdpDisconnected(object? sender, string reason)
        {
            // 在主线程处理 UI 逻辑
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Logger.LogWarning($"会话 {_username} 物理连接断开: {reason}");
                this.Close();
            }));
        }

        private void RdpWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
                Logger.LogInfo($"RdpWindow 关闭中 (User={_username})...");
                RdpCtrl.Disconnect();
            }
            catch (Exception ex)
            {
                Logger.LogError("RdpWindow 断开连接失败", ex);
            }

            WindowClosedEvent?.Invoke(this);
        }
    }
}
