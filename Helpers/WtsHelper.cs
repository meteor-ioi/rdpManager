using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using rdpManager.Helpers;

namespace rdpManager.Helpers
{
    public enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    using System.ComponentModel;
    using System.Runtime.CompilerServices;

    public class WtsSessionInfo : INotifyPropertyChanged
    {
        private int _sessionId;
        private string _userName = string.Empty;
        private WtsConnectState _state;
        private int _clientWidth;
        private int _clientHeight;
        private TimeSpan _runningTime;

        public int SessionId
        {
            get => _sessionId;
            set { _sessionId = value; OnPropertyChanged(); }
        }

        public string UserName
        {
            get => _userName;
            set { _userName = value; OnPropertyChanged(); }
        }

        public WtsConnectState State
        {
            get => _state;
            set 
            { 
                _state = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateColor));
            }
        }

        public int ClientWidth
        {
            get => _clientWidth;
            set 
            { 
                _clientWidth = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(DisplayResolution));
            }
        }

        public int ClientHeight
        {
            get => _clientHeight;
            set 
            { 
                _clientHeight = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(DisplayResolution));
            }
        }

        public TimeSpan RunningTime
        {
            get => _runningTime;
            set 
            { 
                _runningTime = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(RunningTimeText));
            }
        }

        public string StateText => State == WtsConnectState.Active ? "活跃" : (State == WtsConnectState.Disconnected ? "已断开" : State.ToString());
        public string StateColor => State == WtsConnectState.Active ? "#4ADE80" : (State == WtsConnectState.Disconnected ? "#FACC15" : "#888CAA");
        public string DisplayResolution => ClientWidth > 0 && ClientHeight > 0 ? $"{ClientWidth}×{ClientHeight}" : "未知";
        public string RunningTimeText => RunningTime.TotalHours >= 1 ? $"{(int)RunningTime.TotalHours}小时{RunningTime.Minutes}分" : $"{RunningTime.Minutes:00}:{RunningTime.Seconds:00}";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public static class WtsHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WTS_SESSION_INFO
        {
            public int SessionId;
            public string pWinStationName;
            public WtsConnectState State;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_CLIENT_DISPLAY
        {
            public int HorizontalResolution;
            public int VerticalResolution;
            public int ColorDepth;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public short dmOrientation;
            public short dmPaperSize;
            public short dmPaperLength;
            public short dmPaperWidth;
            public short dmScale;
            public short dmCopies;
            public short dmDefaultSource;
            public short dmPrintQuality;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public short dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        private const int DM_PELSWIDTH = 0x00080000;
        private const int DM_PELSHEIGHT = 0x00100000;

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSEnumerateSessions(
            IntPtr hServer,
            int Reserved,
            int Version,
            out IntPtr ppSessionInfo,
            out int pCount
        );

        [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr hServer,
            int sessionId,
            int wtsInfoClass,
            out IntPtr ppBuffer,
            out int pBytesReturned
        );

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, bool bWait);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool bWait);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        public static List<WtsSessionInfo> GetWtsSessions()
        {
            var list = new List<WtsSessionInfo>();
            IntPtr pSessionInfo = IntPtr.Zero;
            int count = 0;

            if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out pSessionInfo, out count))
            {
                try
                {
                    int structSize = Marshal.SizeOf<WTS_SESSION_INFO>();
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr current = pSessionInfo + (i * structSize);
                        var sessionInfo = Marshal.PtrToStructure<WTS_SESSION_INFO>(current);

                        if (sessionInfo.SessionId == 0) continue;

                        string userName = GetSessionUserName(sessionInfo.SessionId);
                        if (string.IsNullOrEmpty(userName)) continue;

                        GetSessionResolution(sessionInfo.SessionId, out int width, out int height);

                        TimeSpan runningTime = TimeSpan.Zero;
                        long logonTime = GetSessionLogonTime(sessionInfo.SessionId);
                        if (logonTime > 0)
                        {
                            try
                            {
                                var logonDateTime = DateTime.FromFileTime(logonTime);
                                runningTime = DateTime.Now - logonDateTime;
                                if (runningTime < TimeSpan.Zero) runningTime = TimeSpan.Zero;
                            }
                            catch
                            {
                                // Ignore conversion issues
                            }
                        }

                        list.Add(new WtsSessionInfo
                        {
                            SessionId = sessionInfo.SessionId,
                            UserName = userName,
                            State = sessionInfo.State,
                            ClientWidth = width,
                            ClientHeight = height,
                            RunningTime = runningTime
                        });
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"GetWtsSessions 发生异常", ex);
                }
                finally
                {
                    if (pSessionInfo != IntPtr.Zero)
                    {
                        WTSFreeMemory(pSessionInfo);
                    }
                }
            }
            return list;
        }

        public static bool DisconnectSession(int sessionId)
        {
            try
            {
                return WTSDisconnectSession(IntPtr.Zero, sessionId, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"DisconnectSession 失败 (SessionId={sessionId})", ex);
                return false;
            }
        }

        public static bool LogoffSession(int sessionId)
        {
            try
            {
                return WTSLogoffSession(IntPtr.Zero, sessionId, false);
            }
            catch (Exception ex)
            {
                Logger.LogError($"LogoffSession 失败 (SessionId={sessionId})", ex);
                return false;
            }
        }

        public static bool TsconToConsole(int sessionId)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "tscon.exe",
                    Arguments = $"{sessionId} /dest:console",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        process.WaitForExit(5000);
                        return process.ExitCode == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"TsconToConsole 失败 (SessionId={sessionId})", ex);
            }
            return false;
        }

        public static bool LockResolution(int w, int h)
        {
            if (w <= 0 || h <= 0) return false;
            try
            {
                DEVMODE devMode = new DEVMODE();
                devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettingsW(null, -1, ref devMode))
                {
                    if (devMode.dmPelsWidth == w && devMode.dmPelsHeight == h)
                    {
                        return true;
                    }
                    devMode.dmPelsWidth = w;
                    devMode.dmPelsHeight = h;
                    devMode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
                    int result = ChangeDisplaySettingsExW(null, ref devMode, IntPtr.Zero, 0, IntPtr.Zero);
                    return result == 0;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"LockResolution 失败 (w={w}, h={h})", ex);
            }
            return false;
        }

        public static void PreventSleep()
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
            }
            catch (Exception ex)
            {
                Logger.LogError("PreventSleep 失败", ex);
            }
        }

        public static void AllowSleep()
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS);
            }
            catch (Exception ex)
            {
                Logger.LogError("AllowSleep 失败", ex);
            }
        }

        private static string GetSessionUserName(int sessionId)
        {
            IntPtr buffer = IntPtr.Zero;
            int bytesReturned = 0;
            try
            {
                if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, 5, out buffer, out bytesReturned))
                {
                    if (buffer != IntPtr.Zero)
                    {
                        return Marshal.PtrToStringUni(buffer) ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"GetSessionUserName 异常 (SessionId={sessionId})", ex);
            }
            finally
            {
                if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
            }
            return string.Empty;
        }

        private static void GetSessionResolution(int sessionId, out int width, out int height)
        {
            width = 0;
            height = 0;
            IntPtr buffer = IntPtr.Zero;
            int bytesReturned = 0;
            try
            {
                if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, 16, out buffer, out bytesReturned))
                {
                    if (buffer != IntPtr.Zero && bytesReturned >= Marshal.SizeOf<WTS_CLIENT_DISPLAY>())
                    {
                        var display = Marshal.PtrToStructure<WTS_CLIENT_DISPLAY>(buffer);
                        width = display.HorizontalResolution;
                        height = display.VerticalResolution;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"GetSessionResolution 异常 (SessionId={sessionId})", ex);
            }
            finally
            {
                if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
            }
        }

        private static long GetSessionLogonTime(int sessionId)
        {
            IntPtr buffer = IntPtr.Zero;
            int bytesReturned = 0;
            try
            {
                if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, 24, out buffer, out bytesReturned))
                {
                    if (buffer != IntPtr.Zero && bytesReturned >= 208)
                    {
                        // LogonTime is at offset 200 (8 bytes) on x64 Systems
                        return Marshal.ReadInt64(buffer, 200);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"GetSessionLogonTime 异常 (SessionId={sessionId})", ex);
            }
            finally
            {
                if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
            }
            return 0;
        }
    }
}
