using System;
using System.Diagnostics;
using System.IO;
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

        /// <summary>
        /// Repair an entry whose exe no longer exists (e.g. SoundRadar was moved) by pointing it
        /// here. A working entry is left alone, so running some other copy never hijacks it.
        /// </summary>
        public static void Refresh()
        {
            string value;
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(value))
                return;
            var path = value.StartsWith("\"") ? value.Substring(1, Math.Max(0, value.IndexOf('"', 1) - 1)) : value.Split(' ')[0];
            if (!File.Exists(path))
                Set(true);
        }
    }
}
