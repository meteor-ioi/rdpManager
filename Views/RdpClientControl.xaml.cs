using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MSTSCLib;
using rdpManager.Helpers;

namespace rdpManager.Views
{
    public partial class RdpClientControl : UserControl
    {
        private AxMSTSCLib.AxMsTscAxNotSafeForScripting? _rdpControl;
        private string _password = string.Empty;

        public string ServerName { get; private set; } = string.Empty;
        public string UserName { get; private set; } = string.Empty;
        public bool IsConnected { get; private set; } = false;

        // 是否处于隐藏（后台保活）状态
        private bool _isHiddenSession = false;
        public bool IsHiddenSession
        {
            get => _isHiddenSession;
            set
            {
                _isHiddenSession = value;
                if (_isHiddenSession)
                {
                    // 虚假断开：降低透明度为0，并禁用鼠标/键盘命中，物理移出可见区域以绕过 WinForms 遮罩问题，但保持在视觉树中继续渲染
                    this.Opacity = 0;
                    this.IsHitTestVisible = false;
                    this.Margin = new System.Windows.Thickness(-10000, 0, 10000, 0);
                }
                else
                {
                    this.Opacity = 1;
                    this.IsHitTestVisible = true;
                    this.Margin = new System.Windows.Thickness(0);
                }
            }
        }

        public event EventHandler? OnRdpConnected;
        public event EventHandler<string>? OnRdpDisconnected;

        private string? _pendingServer;
        private string? _pendingUsername;
        private string? _pendingPassword;
        private bool _pendingEnableUsb;
        private bool _pendingEnableSmartSizing;
        private bool _pendingEnableClipboard;
        private bool _pendingMuteAudio;
        private int _pendingDesktopWidth;
        private int _pendingDesktopHeight;
        private int _pendingDesktopScaleFactor;
        private bool _pendingRedirectMic;
        private bool _pendingRedirectDrives;
        private bool _pendingRedirectPrinters;
        private bool _connectPending = false;

        public RdpClientControl()
        {
            InitializeComponent();
            this.Loaded += RdpClientControl_Loaded;
        }

        private void RdpClientControl_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rdpControl == null)
                {
                    Logger.LogInfo("开始在 WinFormsHost 中实例化 AxMsTscAxNotSafeForScripting 控件...");
                    _rdpControl = new AxMSTSCLib.AxMsTscAxNotSafeForScripting();
                    _rdpControl.BeginInit();
                    RdpHost.Child = _rdpControl;
                    _rdpControl.EndInit();

                    // 让 WinForms 控件铺满容器
                    _rdpControl.Dock = System.Windows.Forms.DockStyle.Fill;

                    // 绑定事件
                    _rdpControl.OnConnected += (s, ev) =>
                    {
                        Logger.LogInfo($"AxMsTscAx 触发 OnConnected 回调: Server={ServerName}");
                        IsConnected = true;
                        OnRdpConnected?.Invoke(this, EventArgs.Empty);
                    };

                    _rdpControl.OnDisconnected += (s, ev) =>
                    {
                        IsConnected = false;
                        string reason = $"连接已断开 (代码: {ev.discReason})";
                        Logger.LogWarning($"AxMsTscAx 触发 OnDisconnected 回调: Server={ServerName}, Reason={reason}");
                        OnRdpDisconnected?.Invoke(this, reason);
                    };
                }

                if (_connectPending)
                {
                    Logger.LogInfo("检测到有缓存的连接请求，立即执行连接。");
                    _connectPending = false;
                    
                    Connect(_pendingServer!, _pendingUsername!, _pendingPassword!, 
                        _pendingEnableUsb, _pendingEnableSmartSizing, _pendingEnableClipboard, _pendingMuteAudio,
                        _pendingDesktopWidth, _pendingDesktopHeight, _pendingDesktopScaleFactor,
                        _pendingRedirectMic, _pendingRedirectDrives, _pendingRedirectPrinters);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("初始化 RDP 控件失败", ex);
                MessageBox.Show($"初始化 RDP 控件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 配置并连接 RDP
        /// </summary>
        public void Connect(string server, string username, string password, 
            bool enableUsb = false, bool enableSmartSizing = true, 
            bool enableClipboard = true, bool muteAudio = true,
            int desktopWidth = 0, int desktopHeight = 0, int desktopScaleFactor = 100,
            bool redirectMic = false, bool redirectDrives = false, bool redirectPrinters = false)
        {
            Logger.LogInfo($"RdpClientControl.Connect() 被调用: Server={server}, Username={username}, EnableUsb={enableUsb}, EnableSmartSizing={enableSmartSizing}, EnableClipboard={enableClipboard}, MuteAudio={muteAudio}, RedirectMic={redirectMic}, RedirectDrives={redirectDrives}, RedirectPrinters={redirectPrinters}");
            
            _pendingServer = server;
            _pendingUsername = username;
            _pendingPassword = password;
            _pendingEnableUsb = enableUsb;
            _pendingEnableSmartSizing = enableSmartSizing;
            _pendingEnableClipboard = enableClipboard;
            _pendingMuteAudio = muteAudio;
            _pendingDesktopWidth = desktopWidth;
            _pendingDesktopHeight = desktopHeight;
            _pendingDesktopScaleFactor = desktopScaleFactor;
            _pendingRedirectMic = redirectMic;
            _pendingRedirectDrives = redirectDrives;
            _pendingRedirectPrinters = redirectPrinters;

            if (_rdpControl == null)
            {
                Logger.LogInfo("WinForms RDP 控件尚未完成 Load，缓存连接请求以待加载完成后执行。");
                _connectPending = true;
                return;
            }

            ServerName = server;
            UserName = username;
            _password = password;

            _rdpControl.Server = server;
            _rdpControl.UserName = username;
            
            if (desktopWidth > 0 && desktopHeight > 0)
            {
                _rdpControl.DesktopWidth = desktopWidth;
                _rdpControl.DesktopHeight = desktopHeight;
            }
            else
            {
                var (w, h) = GetAdaptiveResolution();
                _rdpControl.DesktopWidth = w;
                _rdpControl.DesktopHeight = h;
                Logger.LogInfo($"自适应模式: 远程桌面分辨率设为 {w}x{h}");
            }

            // 设置密码 (通过 COM 接口转换设置明文密码)
            var advancedSettings = (IMsRdpClientAdvancedSettings)_rdpControl.AdvancedSettings;
            advancedSettings.ClearTextPassword = password;

            // RDP 基础优化配置
            var advancedSettings5 = (IMsRdpClientAdvancedSettings5)_rdpControl.AdvancedSettings;
            advancedSettings5.SmartSizing = enableSmartSizing;       // 分辨率自适应缩放
            advancedSettings5.RedirectClipboard = enableClipboard; // 启用双向剪贴板
            advancedSettings5.RedirectPrinters = redirectPrinters;
            advancedSettings5.RedirectDrives = redirectDrives;
            advancedSettings5.RedirectSmartCards = false;

            // 音频优化：1 = 不在本地播放音频（完全静音运行，节省 CPU 开销）
            if (_rdpControl.SecuredSettings != null)
            {
                var securedSettings = (IMsRdpClientSecuredSettings)_rdpControl.SecuredSettings;
                securedSettings.AudioRedirectionMode = muteAudio ? 1 : 0;
            }

            try
            {
                dynamic adv = _rdpControl.AdvancedSettings;
                adv.AudioCaptureRedirectionMode = redirectMic;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"设置音频捕获 (麦克风) 重定向失败: {ex.Message}");
            }

            // 如果开启了外设重定向 (UmWrap 功能)
            if (enableUsb)
            {
                advancedSettings5.RedirectDevices = true; // 允许即插即用外设重定向（USB/摄像头）
            }

            try
            {
                Logger.LogInfo($"调用 ActiveX 控件 Connect(): Server={server}");
                _rdpControl.Connect();
            }
            catch (Exception ex)
            {
                Logger.LogError($"调用 Connect() 抛出异常: {ex.Message}", ex);
                OnRdpDisconnected?.Invoke(this, $"连接尝试失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 主动彻底断开会话
        /// </summary>
        public void Disconnect()
        {
            if (_rdpControl != null && IsConnected)
            {
                try
                {
                    _rdpControl.Disconnect();
                }
                catch
                {
                    // 忽略断开异常
                }
            }
        }

        /// <summary>
        /// 获取自适应模式下的远程桌面分辨率（物理像素）
        /// </summary>
        private (int Width, int Height) GetAdaptiveResolution()
        {
            double dpiScale = 1.0;
            var ps = PresentationSource.FromVisual(this);
            if (ps != null)
                dpiScale = ps.CompositionTarget.TransformToDevice.M11;

            if (this.ActualWidth > 10 && this.ActualHeight > 10)
            {
                int w = (int)Math.Round(this.ActualWidth * dpiScale);
                int h = (int)Math.Round(this.ActualHeight * dpiScale);
                return (w, h);
            }

            int sw = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpiScale);
            int sh = (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpiScale);
            return (sw, sh);
        }

        /// <summary>
        /// 截取当前后台渲染缓冲区的画面作为网格预览缩略图
        /// </summary>
        public BitmapSource? CaptureThumbnail()
        {
            if (_rdpControl == null || !IsConnected) return null;

            try
            {
                // 获取控件的实际物理尺寸
                int width = _rdpControl.Width;
                int height = _rdpControl.Height;

                if (width <= 0 || height <= 0)
                {
                    width = 800; // 默认大小兜底
                    height = 600;
                }

                using (Bitmap bmp = new Bitmap(width, height))
                {
                    // 使用 WinForms 控件自带的 DrawToBitmap 从显存/渲染表面抓取画面
                    _rdpControl.DrawToBitmap(bmp, new Rectangle(0, 0, width, height));
                    
                    return ConvertBitmapToSource(bmp);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将 GDI Bitmap 转换为 WPF 能够渲染的 BitmapSource
        /// </summary>
        private BitmapSource ConvertBitmapToSource(Bitmap bitmap)
        {
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject([In] IntPtr hObject);
    }
}
