using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;
using SoundRadar.Interop;

namespace SoundRadar.Core
{
    /// <summary>Hotkey text such as "Ctrl+Shift+F9" or "Ctrl+=", parsed to RegisterHotKey arguments.</summary>
    internal static class HotkeyText
    {
        // Friendlier names for keys whose WPF names are cryptic.
        private static readonly Dictionary<Key, string> Friendly = new Dictionary<Key, string>
        {
            [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4",
            [Key.D5] = "5", [Key.D6] = "6", [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",
            [Key.OemPlus] = "=", [Key.OemMinus] = "-", [Key.OemOpenBrackets] = "[", [Key.OemCloseBrackets] = "]",
            [Key.OemComma] = ",", [Key.OemPeriod] = ".", [Key.OemQuestion] = "/", [Key.OemSemicolon] = ";",
            [Key.OemQuotes] = "'", [Key.OemPipe] = "\\", [Key.OemTilde] = "`", [Key.Return] = "Enter",
            [Key.Next] = "PageDown", [Key.Prior] = "PageUp", [Key.Capital] = "CapsLock",
        };

        private static readonly Dictionary<string, Key> FromFriendly =
            Friendly.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

        private static readonly KeyConverter Converter = new KeyConverter();

        public static bool IsModifier(Key key) =>
            key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;

        public static string Format(ModifierKeys modifiers, Key key)
        {
            var parts = new List<string>();
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            if (key != Key.None)
                parts.Add(Friendly.TryGetValue(key, out var name) ? name : Converter.ConvertToInvariantString(key));
            return string.Join("+", parts);
        }

        public static bool TryParse(string text, out uint modifiers, out uint virtualKey)
        {
            modifiers = 0;
            virtualKey = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            // "+" itself can't be a key here (we write it as "="), so splitting on '+' is safe.
            var parts = text.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            Key? key = null;
            foreach (var part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; continue;
                    case "alt": modifiers |= Native.MOD_ALT; continue;
                    case "shift": modifiers |= Native.MOD_SHIFT; continue;
                    case "win": case "windows": modifiers |= Native.MOD_WIN; continue;
                }
                if (key != null)
                    return false; // two non-modifier keys
                if (FromFriendly.TryGetValue(part, out var friendly))
                {
                    key = friendly;
                    continue;
                }
                try
                {
                    key = (Key)Converter.ConvertFromInvariantString(part);
                }
                catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException || ex is FormatException)
                {
                    return false;
                }
            }
            if (key == null || key == Key.None)
                return false;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
            return virtualKey != 0;
        }
    }

    /// <summary>
    /// Global hotkeys via RegisterHotKey: Windows only tells us about our own combinations,
    /// so, unlike a keyboard hook, we never see anything else the user types.
    /// </summary>
    internal sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _window;
        private readonly Dictionary<int, string> _registered = new Dictionary<int, string>();
        private int _nextId = 1;

        public HotkeyManager()
        {
            _window = new HwndSource(new HwndSourceParameters("SoundRadarHotkeys") { ParentWindow = Native.HWND_MESSAGE, WindowStyle = 0 });
            _window.AddHook(WndProc);
        }

        public event Action<string> Triggered;

        /// <summary>Register <c>{action: hotkey}</c>; returns the actions that couldn't be registered.</summary>
        public List<string> Apply(IDictionary<string, string> mapping)
        {
            foreach (var id in _registered.Keys)
                Native.UnregisterHotKey(_window.Handle, id);
            _registered.Clear();

            var rejected = new List<string>();
            foreach (var pair in mapping)
            {
                if (string.IsNullOrWhiteSpace(pair.Value))
                    continue;
                var id = _nextId++;
                if (HotkeyText.TryParse(pair.Value, out var modifiers, out var vk) &&
                    Native.RegisterHotKey(_window.Handle, id, modifiers | Native.MOD_NOREPEAT, vk))
                    _registered[id] = pair.Key;
                else
                    rejected.Add(pair.Key); // unparseable, or already taken by another app
            }
            return rejected;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                Triggered?.Invoke(action);
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in _registered.Keys)
                Native.UnregisterHotKey(_window.Handle, id);
            _registered.Clear();
            _window.Dispose();
        }
    }
}
