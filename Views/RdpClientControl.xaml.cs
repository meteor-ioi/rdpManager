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

                    // 强制 WPF 布局更新
                    RdpHost?.InvalidateVisual();
                    RdpHost?.UpdateLayout();
                    // 延迟到渲染完成后，强制刷新底层 Win32 HWND（InvalidateVisual 仅影响 WPF 层，不触发 HWND 的 WM_PAINT）
                    Dispatcher.InvokeAsync(ForceHwndRepaint, System.Windows.Threading.DispatcherPriority.Render);
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
                        Logger.LogInfo($"AxMsTscAx 触发 OnConnected 回调: Server={ServerName}, ControlSize={_rdpControl?.Width}x{_rdpControl?.Height}");
                        IsConnected = true;
                        OnRdpConnected?.Invoke(this, EventArgs.Empty);
                        // 连接建立后强制刷新 HWND 画面（ActiveX 控件首帧可能不自动渲染）
                        Dispatcher.InvokeAsync(ForceHwndRepaint, System.Windows.Threading.DispatcherPriority.Render);
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
                    
                    // 延迟到用户输入优先级执行连接——比 Loaded/Render 更低，确保 WPF 布局和渲染管道已完整结算 HWND 尺寸
                    Dispatcher.InvokeAsync(() =>
                    {
                        Logger.LogInfo($"执行缓存的 RDP 连接请求: Host大小={RdpHost?.ActualWidth}x{RdpHost?.ActualHeight}");
                        Connect(_pendingServer!, _pendingUsername!, _pendingPassword!, 
                            _pendingEnableUsb, _pendingEnableSmartSizing, _pendingEnableClipboard, _pendingMuteAudio,
                            _pendingDesktopWidth, _pendingDesktopHeight, _pendingDesktopScaleFactor);
                    }, System.Windows.Threading.DispatcherPriority.Input);
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
            int desktopWidth = 0, int desktopHeight = 0, int desktopScaleFactor = 100)
        {
            Logger.LogInfo($"RdpClientControl.Connect() 被调用: Server={server}, Username={username}, EnableUsb={enableUsb}, EnableSmartSizing={enableSmartSizing}, EnableClipboard={enableClipboard}, MuteAudio={muteAudio}");
            if (_rdpControl == null)
            {
                Logger.LogInfo("WinForms RDP 控件尚未完成 Load，缓存连接请求以待加载完成后执行。");
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
                _connectPending = true;
                return;
            }

            ServerName = server;
            UserName = username;
            _password = password;

            _rdpControl.Server = server;
            _rdpControl.UserName = username;
            
            // 如果未指定分辨率，使用本机系统主屏幕分辨率作为远程桌面画布尺寸，配合 SmartSizing 适应控件
            if (desktopWidth <= 0 || desktopHeight <= 0)
            {
                desktopWidth = (int)SystemParameters.PrimaryScreenWidth;
                desktopHeight = (int)SystemParameters.PrimaryScreenHeight;
            }

            _rdpControl.DesktopWidth = desktopWidth;
            _rdpControl.DesktopHeight = desktopHeight;
            
            // 明确指定颜色深度为 32 位，解决部分系统默认低色深导致的黑屏问题
            var rdpClient = _rdpControl.GetOcx() as IMsRdpClient;
            if (rdpClient != null)
            {
                rdpClient.ColorDepth = 32;
            }

            // 设置密码 (通过 COM 接口转换设置明文密码)
            var advancedSettings = (IMsRdpClientAdvancedSettings)_rdpControl.AdvancedSettings;
            advancedSettings.ClearTextPassword = password;

            // 启用 CredSSP 支持 (避免本地环境因为 NLA 导致黑屏或闪退)
            try
            {
                var advancedSettings7 = _rdpControl.AdvancedSettings as IMsRdpClientAdvancedSettings7;
                if (advancedSettings7 != null)
                {
                    advancedSettings7.EnableCredSspSupport = true;
                }
            }
            catch { }

            // RDP 基础优化配置
            var advancedSettings5 = (IMsRdpClientAdvancedSettings5)_rdpControl.AdvancedSettings;
            advancedSettings5.SmartSizing = enableSmartSizing;       // 分辨率自适应缩放
            advancedSettings5.RedirectClipboard = enableClipboard; // 启用双向剪贴板
            advancedSettings5.RedirectPrinters = false; // 禁用打印机重定向以优化速度
            advancedSettings5.RedirectSmartCards = false;
            advancedSettings5.BitmapPeristence = 0; // 禁用位图缓存，防止本地回环连接时缓存损坏导致黑屏
            advancedSettings5.AuthenticationLevel = 0; // 跳过服务器证书验证（本地回环使用自签名证书，标准验证会导致连接静默挂起）

            // 应用 DPI 缩放配置 (需要高级接口或动态绑定以兼容旧系统)
            if (desktopScaleFactor > 100)
            {
                try
                {
                    dynamic advancedSettingsDynamic = _rdpControl.AdvancedSettings;
                    advancedSettingsDynamic.DesktopScaleFactor = (uint)desktopScaleFactor;
                    advancedSettingsDynamic.DeviceScaleFactor = 100u; // 通常设备缩放比为 100%，以保证界面元素不会过小
                    Logger.LogInfo($"已注入 DPI 缩放比: {desktopScaleFactor}%");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"当前 RDP 客户端版本不支持自定义缩放比，将忽略缩放设置: {ex.Message}");
                }
            }

            // 音频优化：1 = 不在本地播放音频（完全静音运行，节省 CPU 开销）
            if (_rdpControl.SecuredSettings != null)
            {
                var securedSettings = (IMsRdpClientSecuredSettings)_rdpControl.SecuredSettings;
                securedSettings.AudioRedirectionMode = muteAudio ? 1 : 0;
            }

            // 如果开启了外设重定向 (UmWrap 功能)
            if (enableUsb)
            {
                advancedSettings5.RedirectDevices = true; // 允许即插即用外设重定向（USB/摄像头）
            }

            try
            {
                Logger.LogInfo($"调用 ActiveX 控件 Connect(): Server={server}, ControlSize={_rdpControl.Width}x{_rdpControl.Height}, HandleCreated={_rdpControl.IsHandleCreated}");
                _rdpControl.Connect();
                // 连接发起后延迟强制刷新 HWND 确保初始帧渲染
                Dispatcher.InvokeAsync(ForceHwndRepaint, System.Windows.Threading.DispatcherPriority.Render);
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_FRAME = 0x0400;

        /// <summary>
        /// 强制刷新底层 Win32 HWND（绕过 WPF 渲染管道，直接触发 WM_PAINT）
        /// </summary>
        private void ForceHwndRepaint()
        {
            try
            {
                if (_rdpControl != null && _rdpControl.IsHandleCreated)
                {
                    IntPtr hwnd = _rdpControl.Handle;
                    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                        RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_ERASE | RDW_FRAME);
                    Logger.LogInfo($"已强制刷新 RDP HWND=0x{hwnd:X}, Size={_rdpControl.Width}x{_rdpControl.Height}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"ForceHwndRepaint 失败: {ex.Message}");
            }
        }

        [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject([In] IntPtr hObject);
    }
}
