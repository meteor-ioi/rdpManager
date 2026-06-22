using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace rdpManager.Helpers
{
    public enum ThemeMode
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    public static class ThemeManager
    {
        private const string REG_PATH = @"Software\LocalRDP";
        private const string THEME_VAL = "ThemeMode";

        public static ThemeMode CurrentMode { get; private set; } = ThemeMode.System;

        public static void Initialize()
        {
            LoadSettings();
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        }

        private static void LoadSettings()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(REG_PATH))
                {
                    if (key != null)
                    {
                        var val = key.GetValue(THEME_VAL);
                        if (val != null && int.TryParse(val.ToString(), out int modeInt))
                        {
                            if (Enum.IsDefined(typeof(ThemeMode), modeInt))
                            {
                                CurrentMode = (ThemeMode)modeInt;
                                return;
                            }
                        }
                    }
                }
            }
            catch { }
            CurrentMode = ThemeMode.System;
        }

        public static void SaveThemeMode(ThemeMode mode)
        {
            CurrentMode = mode;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(REG_PATH))
                {
                    key.SetValue(THEME_VAL, (int)mode, RegistryValueKind.DWord);
                }
            }
            catch { }
            ApplyTheme();
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General && CurrentMode == ThemeMode.System)
            {
                ApplyTheme();
            }
        }

        private static bool IsSystemDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("AppsUseLightTheme");
                        if (val != null)
                        {
                            return (int)val == 0;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static void ApplyTheme()
        {
            bool useDark = false;
            switch (CurrentMode)
            {
                case ThemeMode.System:
                    useDark = IsSystemDark();
                    break;
                case ThemeMode.Dark:
                    useDark = true;
                    break;
                case ThemeMode.Light:
                    useDark = false;
                    break;
            }

            string themeName = useDark ? "Dark.xaml" : "Light.xaml";
            var uri = new Uri($"pack://application:,,,/Themes/{themeName}", UriKind.Absolute);
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dict = new ResourceDictionary { Source = uri };
                var appDicts = Application.Current.Resources.MergedDictionaries;
                
                // 替换索引0的字典
                if (appDicts.Count > 0)
                {
                    appDicts[0] = dict;
                }
                else
                {
                    appDicts.Add(dict);
                }
            });
        }
    }
}
