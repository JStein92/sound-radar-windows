using System.Diagnostics;
using Microsoft.Win32;

namespace SoundRadar.Core
{
    /// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SoundRadar";

        private static string Command => $"\"{Process.GetCurrentProcess().MainModule.FileName}\" --minimized";

        public static bool IsEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
        }

        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                    key.SetValue(ValueName, Command);
                else if (key.GetValue(ValueName) != null)
                    key.DeleteValue(ValueName);
            }
        }

        /// <summary>Keep an existing entry pointing at this exe (e.g. after the install folder changes).</summary>
        public static void Refresh()
        {
            if (IsEnabled)
                Set(true);
        }
    }
}
