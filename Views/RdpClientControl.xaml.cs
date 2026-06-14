using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MSTSCLib;

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
                    // 虚假断开：降低透明度为0，并禁用鼠标/键盘命中，移到侧边，但保持在视觉树中以继续渲染
                    this.Opacity = 0;
                    this.IsHitTestVisible = false;
                }
                else
                {
                    this.Opacity = 1;
                    this.IsHitTestVisible = true;
                }
            }
        }

        public event EventHandler? OnRdpConnected;
        public event EventHandler<string>? OnRdpDisconnected;

        public RdpClientControl()
        {
            InitializeComponent();
            this.Loaded += RdpClientControl_Loaded;
        }

        private void RdpClientControl_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _rdpControl = new AxMSTSCLib.AxMsTscAxNotSafeForScripting();
                _rdpControl.BeginInit();
                RdpHost.Child = _rdpControl;
                _rdpControl.EndInit();

                // 绑定事件
                _rdpControl.OnConnect += (s, ev) =>
                {
                    IsConnected = true;
                    OnRdpConnected?.Invoke(this, EventArgs.Empty);
                };

                _rdpControl.OnDisconnect += (s, ev) =>
                {
                    IsConnected = false;
                    string reason = "未知原因";
                    if (_rdpControl != null)
                    {
                        reason = _rdpControl.GetErrorDescription((uint)ev.discReason, (uint)_rdpControl.ExtendedDisconnectReason);
                    }
                    OnRdpDisconnected?.Invoke(this, reason);
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化 RDP 控件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 配置并连接 RDP
        /// </summary>
        public void Connect(string server, string username, string password, bool enableUsb = false)
        {
            if (_rdpControl == null) return;

            ServerName = server;
            UserName = username;
            _password = password;

            _rdpControl.Server = server;
            _rdpControl.UserName = username;

            // 设置密码 (通过 COM 接口转换设置明文密码)
            var advancedSettings = (IMsRdpClientAdvancedSettings)_rdpControl.AdvancedSettings;
            advancedSettings.ClearTextPassword = password;

            // RDP 基础优化配置
            var advancedSettings5 = (IMsRdpClientAdvancedSettings5)_rdpControl.AdvancedSettings;
            advancedSettings5.SmartSizing = true;       // 分辨率自适应缩放
            advancedSettings5.RedirectClipboard = true; // 启用双向剪贴板
            advancedSettings5.RedirectPrinters = false; // 禁用打印机重定向以优化速度
            advancedSettings5.RedirectSmartCards = false;

            // 音频优化：1 = 不在本地播放音频（完全静音运行，节省 CPU 开销）
            _rdpControl.SecuredSettings3.AudioRedirectionMode = 1;

            // 如果开启了外设重定向 (UmWrap 功能)
            if (enableUsb)
            {
                advancedSettings5.RedirectDevices = true; // 允许即插即用外设重定向（USB/摄像头）
            }

            try
            {
                _rdpControl.Connect();
            }
            catch (Exception ex)
            {
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

        [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject([In] IntPtr hObject);
    }
}
