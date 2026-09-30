using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundRadar.Audio;
using SoundRadar.Core;
using SoundRadar.Overlay;
using SoundRadar.UI;
using Forms = System.Windows.Forms;

namespace SoundRadar
{
    /// <summary>Wires the audio engine, the two bars, the settings window, hotkeys and tray icon together.</summary>
    internal sealed class Controller : IDisposable
    {
        // Same attack/decay feel as the LED strips (EMA per packet at ~86 packets/s).
        private const double AlphaRise = 0.3;
        private const double AlphaFall = 0.1;
        private const double ReferenceRate = 86;

        private readonly BarWindow[] _bars = { new BarWindow(left: true), new BarWindow(left: false) };
        private readonly double[] _levels = new double[2];
        private readonly FrequencyColor[] _frequencyColors = { new FrequencyColor(), new FrequencyColor() };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _topmostTimer;
        private readonly HotkeyManager _hotkeys;
        private readonly ControlPanel _panel;
        private Forms.NotifyIcon _tray;
        private Forms.ToolStripMenuItem _pauseItem;
        private double _lastFrame;
        private bool _trayHintShown;
        private bool _disposed;

        public Controller(bool startHidden)
        {
            Settings = ConfigStore.LoadSettings();
            Profile = ConfigStore.LoadProfile(Settings.CurrentProfile);

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (s, e) => Save();

            _hotkeys = new HotkeyManager();
            _hotkeys.Triggered += RunAction;
            _hotkeys.Apply(Settings.Hotkeys);

            ApplyProfile();
            ApplyLayout();
            Engine.SetSource(Settings.Source, Settings.SourceApp);
            Engine.Start();

            _panel = new ControlPanel(this);
            BuildTray();
            ApplyEnabled();

            CompositionTarget.Rendering += OnFrame;
            _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _topmostTimer.Tick += (s, e) => KeepOnTop();
            _topmostTimer.Start();
            SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;

            if (!startHidden)
                ShowPanel();
        }

        public AppSettings Settings { get; }
        public Profile Profile { get; private set; }
        public AudioEngine Engine { get; } = new AudioEngine();
        public bool IsShuttingDown { get; private set; }

        // ---- applying state -------------------------------------------------------------

        private void ApplyProfile()
        {
            var p = Profile;
            Engine.Sensitivity = p.Sensitivity;
            Engine.AutoSensitivity = p.AutoSensitivity;
            Engine.ShowDifference = p.ShowDifference;
            Engine.QuietBoost = p.QuietBoost;
            foreach (var bar in _bars)
            {
                bar.ColorName = p.Color;
                bar.Mode = p.Mode;
                bar.Segments = p.Segments;
                bar.Opacity = p.Brightness / 100.0;
                bar.BackgroundOpacity = p.BackgroundOpacity / 100.0;
                if (bar.Visible)
                    bar.Render();
            }
        }

        /// <summary>(left bar's screen, right bar's screen).</summary>
        private (Forms.Screen left, Forms.Screen right) BarScreens()
        {
            var screens = Forms.Screen.AllScreens;
            if (Settings.Monitor == AppSettings.AllMonitors && screens.Length > 0)
            {
                // Outer edges of the whole desktop; prefer the primary when displays are stacked.
                var leftmost = screens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Primary ? 0 : 1).First();
                var rightmost = screens.OrderByDescending(s => s.Bounds.Right).ThenBy(s => s.Primary ? 0 : 1).First();
                return (leftmost, rightmost);
            }
            var chosen = screens.FirstOrDefault(s => s.DeviceName == Settings.Monitor) ?? Forms.Screen.PrimaryScreen;
            return (chosen, chosen);
        }

        private void ApplyLayout()
        {
            var (left, right) = BarScreens();
            _bars[0].Place(left.Bounds, Settings.BarWidth, Settings.BarLength, Settings.EdgeMargin);
            _bars[1].Place(right.Bounds, Settings.BarWidth, Settings.BarLength, Settings.EdgeMargin);
            foreach (var bar in _bars)
                if (bar.Visible)
                    bar.Render();
        }

        private void ApplyEnabled()
        {
            foreach (var bar in _bars)
                bar.SetVisible(Settings.Enabled);
            KeepOnTop();
            _pauseItem.Text = Settings.Enabled ? "Pause" : "Resume";
        }

        private void KeepOnTop()
        {
            foreach (var bar in _bars)
                bar.KeepOnTop();
        }

        private void OnDisplaysChanged(object sender, EventArgs e)
        {
            _panel.Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyLayout();
                if (_panel.IsVisible)
                    _panel.Sync();
            }));
        }

        // ---- per frame ------------------------------------------------------------------

        private void OnFrame(object sender, EventArgs e)
        {
            var now = _clock.Elapsed.TotalSeconds;
            var dt = Math.Min(0.1, now - _lastFrame);
            _lastFrame = now;
            Engine.TakeLevels(out var peakLeft, out var peakRight, out var freqLeft, out var freqRight);
            if (!Settings.Enabled)
                return;

            // Auto sensitivity drifts the value on the audio thread; keep the profile in step.
            if (Profile.AutoSensitivity)
                Profile.Sensitivity = Engine.Sensitivity;

            var useFrequency = Profile.Color == ColorSchemes.Frequency;
            var targets = new[] { peakLeft, peakRight };
            var freqs = new[] { freqLeft, freqRight };
            for (var i = 0; i < 2; i++)
            {
                var rate = targets[i] > _levels[i] ? AlphaRise : AlphaFall;
                var alpha = 1 - Math.Pow(1 - rate, dt * ReferenceRate);
                _levels[i] += alpha * (targets[i] - _levels[i]);
                _bars[i].Level = _levels[i];
                _bars[i].SolidColor = useFrequency ? _frequencyColors[i].Update(freqs[i], dt) : (Rgb?)null;
                _bars[i].Render();
            }
        }

        // ---- changes from the panel / hotkeys ---------------------------------------------

        public void ChangeProfile(Action<Profile> change)
        {
            var wasAuto = Profile.AutoSensitivity;
            change(Profile);
            Profile.Normalize();
            if (Profile.AutoSensitivity != wasAuto)
                Engine.ResetAutoSensitivity();
            ApplyProfile();
            ScheduleSave();
        }

        public void ChangeSettings(Action<AppSettings> change)
        {
            var (source, app) = (Settings.Source, Settings.SourceApp);
            change(Settings);
            Settings.Normalize();
            if (Settings.Source != source || Settings.SourceApp != app)
                Engine.SetSource(Settings.Source, Settings.SourceApp);
            else
                ApplyLayout();
            ScheduleSave();
        }

        public void SelectProfile(int index)
        {
            Save(); // flush pending edits to the profile we're leaving
            Settings.CurrentProfile = Math.Max(0, Math.Min(ConfigStore.ProfileCount - 1, index));
            Profile = ConfigStore.LoadProfile(Settings.CurrentProfile);
            Engine.ResetAutoSensitivity();
            ApplyProfile();
            ScheduleSave();
            _panel.Sync();
        }

        /// <summary>Returns the actions whose hotkeys couldn't be registered.</summary>
        public List<string> SetHotkeys(Dictionary<string, string> mapping)
        {
            Settings.Hotkeys = mapping;
            ScheduleSave();
            return _hotkeys.Apply(mapping);
        }

        public List<string> ListAudioApps()
        {
            try
            {
                return Wasapi.ListAudioSessions().Select(s => s.ExeName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is InvalidCastException)
            {
                return new List<string>();
            }
        }

        public void RunAction(string action)
        {
            var p = Profile;
            switch (action)
            {
                case "increase_brightness": ChangeProfile(x => x.Brightness += 5); break;
                case "decrease_brightness": ChangeProfile(x => x.Brightness -= 5); break;
                case "increase_sensitivity": ChangeProfile(x => x.Sensitivity += 2); break;
                case "decrease_sensitivity": ChangeProfile(x => x.Sensitivity -= 2); break;
                case "switch_mode_left": ChangeProfile(x => x.Mode = Cycle(Modes.All, p.Mode, -1)); break;
                case "switch_mode_right": ChangeProfile(x => x.Mode = Cycle(Modes.All, p.Mode, 1)); break;
                case "switch_color_left": ChangeProfile(x => x.Color = Cycle(ColorSchemes.Names, p.Color, -1)); break;
                case "switch_color_right": ChangeProfile(x => x.Color = Cycle(ColorSchemes.Names, p.Color, 1)); break;
                case "toggle_show_difference": ChangeProfile(x => x.ShowDifference = !x.ShowDifference); break;
                case "toggle_on_off":
                    Settings.Enabled = !Settings.Enabled;
                    ApplyEnabled();
                    ScheduleSave();
                    break;
                case "select_next_profile": SelectProfile(Settings.CurrentProfile + 1); return;
                case "select_previous_profile": SelectProfile(Settings.CurrentProfile - 1); return;
            }
            _panel.Sync();
        }

        private static string Cycle(IReadOnlyList<string> items, string current, int step)
        {
            var index = Math.Max(0, items.ToList().IndexOf(current));
            return items[(index + step + items.Count) % items.Count];
        }

        // ---- persistence ------------------------------------------------------------------

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Save()
        {
            _saveTimer.Stop();
            try
            {
                ConfigStore.SaveProfile(Settings.CurrentProfile, Profile);
                ConfigStore.SaveSettings(Settings);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Try again on the next change rather than interrupting the user.
            }
        }

        // ---- tray & lifecycle -------------------------------------------------------------

        private void BuildTray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Settings...", null, (s, e) => ShowPanel());
            _pauseItem = new Forms.ToolStripMenuItem("Pause", null, (s, e) => RunAction("toggle_on_off"));
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit SoundRadar", null, (s, e) => Quit());

            _tray = new Forms.NotifyIcon
            {
                Icon = App.LoadIcon(Forms.SystemInformation.SmallIconSize.Width),
                Text = "SoundRadar",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                    ShowPanel();
            };
        }

        public void ShowPanel()
        {
            _panel.Sync();
            _panel.Show();
            if (_panel.WindowState == WindowState.Minimized)
                _panel.WindowState = WindowState.Normal;
            _panel.Activate();
        }

        public void Quit()
        {
            // Flag first so the settings window lets itself close instead of hiding.
            IsShuttingDown = true;
            Application.Current.Shutdown();
        }

        public void NotifyHiddenToTray()
        {
            if (_trayHintShown)
                return;
            _trayHintShown = true;
            _tray.ShowBalloonTip(4000, "SoundRadar is still running",
                "The bars stay on screen. Use the tray icon to reopen settings or quit.", Forms.ToolTipIcon.None);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            IsShuttingDown = true;
            CompositionTarget.Rendering -= OnFrame;
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            _topmostTimer.Stop();
            Save();
            _hotkeys.Dispose();
            Engine.Stop();
            foreach (var bar in _bars)
                bar.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _panel.Close();
        }
    }
}
