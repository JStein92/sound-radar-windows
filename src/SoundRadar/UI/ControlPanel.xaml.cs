using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoundRadar.Audio;
using SoundRadar.Core;
using Forms = System.Windows.Forms;

namespace SoundRadar.UI
{
    internal sealed class Choice
    {
        public Choice(string key, string label)
        {
            Key = key;
            Label = label;
        }

        public string Key { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    /// <summary>A named item with a horizontal gradient swatch (color schemes, frequency palettes).</summary>
    internal sealed class SwatchChoice
    {
        public SwatchChoice(string name, IEnumerable<(double t, Rgb color)> stops)
        {
            Name = name;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            foreach (var (t, rgb) in stops)
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(rgb.R, rgb.G, rgb.B), t));
            brush.Freeze();
            Swatch = brush;
        }

        public string Name { get; }
        public Brush Swatch { get; }
        public override string ToString() => Name; // what screen readers announce
    }

    /// <summary>Lets a WinForms dialog be owned by a WPF window.</summary>
    internal sealed class Win32Owner : Forms.IWin32Window
    {
        public Win32Owner(Window window) => Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        public IntPtr Handle { get; }
    }

    internal partial class ControlPanel : Window
    {
        private static readonly Choice[] SourceChoices =
        {
            new Choice(Sources.All, "All apps"),
            new Choice(Sources.App, "One app only (e.g. just the game)"),
            new Choice(Sources.Device, "Output device (legacy)"),
        };

        private static readonly Choice[] ModeChoices =
        {
            new Choice("Bottom", "Fill from bottom"),
            new Choice("Center", "Grow from center"),
            new Choice("Top", "Fill from top"),
        };

        private readonly Controller _c;
        private readonly DispatcherTimer _liveTimer;
        private bool _syncing;
        private string _colorItemsPalette; // palette the Frequency swatch was drawn with
        private int[] _customColors; // the color dialog's custom swatches, kept for this session

        public ControlPanel(Controller controller)
        {
            _c = controller;
            // Sliders raise ValueChanged while the XAML loads (Minimum coerces Value);
            // ignore those so they can't overwrite the profile.
            _syncing = true;
            InitializeComponent();
            _syncing = false;
            Icon = App.LoadImageSource();
            SourceInitialized += (s, e) => Interop.Native.UseDarkTitleBar(this);

            SourceCombo.ItemsSource = SourceChoices;
            ModeCombo.ItemsSource = ModeChoices;
            PaletteCombo.ItemsSource = FrequencyPalettes.All
                .Select(p => new SwatchChoice(p.Name, Enumerable.Range(0, 25).Select(i => (i / 24.0, p.ColorAt(i / 24.0)))))
                .ToList();

            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _liveTimer.Tick += (s, e) => RefreshLive();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible)
                    _liveTimer.Start();
                else
                    _liveTimer.Stop();
            };
        }

        // ---- sync ----------------------------------------------------------------------

        /// <summary>Pull every control's value from the controller.</summary>
        public void Sync()
        {
            var s = _c.Settings;
            var p = _c.Profile;
            _syncing = true;
            try
            {
                ProfileCombo.ItemsSource = Enumerable.Range(0, ConfigStore.ProfileCount).Select(_c.ProfileDisplayName).ToList();
                ProfileCombo.SelectedIndex = s.CurrentProfile;
                PowerToggle.IsChecked = s.Enabled;
                PowerToggle.Content = s.Enabled ? "On" : "Off";

                SourceCombo.SelectedItem = SourceChoices.FirstOrDefault(c => c.Key == s.Source) ?? SourceChoices[0];
                AppRow.Visibility = s.Source == Sources.App ? Visibility.Visible : Visibility.Collapsed;
                if (AppCombo.Text != s.SourceApp)
                    AppCombo.Text = s.SourceApp;

                SensitivitySlider.Value = Math.Round(p.Sensitivity);
                SensitivitySlider.IsEnabled = !p.AutoSensitivity;
                AutoSensitivityCheck.IsChecked = p.AutoSensitivity;
                QuietBoostCheck.IsChecked = p.QuietBoost;
                RadarCheck.IsChecked = p.ShowDifference;

                if (_colorItemsPalette != p.FrequencyPalette)
                {
                    // The Frequency item's swatch shows the chosen palette.
                    var palette = FrequencyPalettes.Get(p.FrequencyPalette);
                    ColorCombo.ItemsSource = ColorSchemes.All.Select(sc => new SwatchChoice(sc.Name, ColorSchemes.SwatchStops(sc, palette))).ToList();
                    _colorItemsPalette = p.FrequencyPalette;
                }
                ColorCombo.SelectedIndex = Math.Max(0, ColorSchemes.Names.ToList().IndexOf(p.Color));
                var isFrequency = p.Color == ColorSchemes.Frequency;
                PaletteLabel.Visibility = PaletteCombo.Visibility = isFrequency ? Visibility.Visible : Visibility.Collapsed;
                PaletteCombo.SelectedIndex = Math.Max(0, FrequencyPalettes.Names.ToList().IndexOf(p.FrequencyPalette));
                ModeCombo.SelectedItem = ModeChoices.FirstOrDefault(c => c.Key == p.Mode) ?? ModeChoices[0];
                SegmentsSlider.Value = p.Segments;
                BrightnessSlider.Value = p.Brightness;
                BackgroundSlider.Value = p.BackgroundOpacity;
                var background = Rgb.FromHex(p.BackgroundColor);
                BackgroundSwatch.Background = new SolidColorBrush(Color.FromRgb(background.R, background.G, background.B));

                FillMonitors();
                WidthSlider.Value = s.BarWidth;
                TopSlider.Value = s.TopMargin;
                BottomSlider.Value = s.BottomMargin;
                MarginSlider.Value = s.EdgeMargin;
                AutostartCheck.IsChecked = Autostart.IsEnabled;
            }
            finally
            {
                _syncing = false;
            }
            UpdateValueLabels();
            RefreshLive();
        }

        private void UpdateValueLabels()
        {
            if (!IsInitialized)
                return; // some controls don't exist yet
            SensitivityValue.Text = $"{SensitivitySlider.Value:0}";
            SegmentsValue.Text = SegmentsSlider.Value == 0 ? "Smooth" : $"{SegmentsSlider.Value:0}";
            BrightnessValue.Text = $"{BrightnessSlider.Value:0}%";
            BackgroundValue.Text = $"{BackgroundSlider.Value:0}%";
            WidthValue.Text = $"{WidthSlider.Value:0} px";
            TopValue.Text = $"{TopSlider.Value:0} px";
            BottomValue.Text = $"{BottomSlider.Value:0} px";
            MarginValue.Text = $"{MarginSlider.Value:0} px";
        }

        private void FillMonitors()
        {
            var screens = Forms.Screen.AllScreens;
            var choices = new List<Choice>();
            // Number displays left to right (Windows' own \\.\DISPLAYn numbers are arbitrary).
            var ordered = screens.OrderBy(sc => sc.Bounds.Left).ThenBy(sc => sc.Bounds.Top).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var screen = ordered[i];
                var position = ordered.Count == 1 ? "" : i == 0 ? " (left)" : i == ordered.Count - 1 ? " (right)" : " (middle)";
                var label = $"Display {i + 1}{position}: {screen.Bounds.Width}x{screen.Bounds.Height}";
                if (screen.Primary)
                    label += "  - primary";
                choices.Add(new Choice(screen.DeviceName, label));
            }
            if (screens.Length > 1 || _c.Settings.Monitor == AppSettings.AllMonitors)
                choices.Add(new Choice(AppSettings.AllMonitors, "All displays - bars on the outer edges"));

            MonitorCombo.ItemsSource = choices;
            var primaryName = Forms.Screen.PrimaryScreen.DeviceName;
            MonitorCombo.SelectedItem = choices.FirstOrDefault(c => c.Key == _c.Settings.Monitor)
                ?? choices.FirstOrDefault(c => c.Key == primaryName)
                ?? choices.FirstOrDefault();
        }

        private void RefreshLive()
        {
            if (!IsVisible)
                return;
            var engine = _c.Engine;
            var enabled = _c.Settings.Enabled;
            StatusText.Text = enabled ? engine.Status : "Paused";
            StatusText.Foreground = (Brush)FindResource(enabled && engine.StatusIsError ? "ErrorFg" : "OkFg");

            var mono = Wasapi.WindowsMonoEnabled();
            if (_c.Settings.Source == Sources.Device)
                MonoText.Text = mono
                    ? "Windows Mono audio is ON, so this source sees identical left/right. Switch to \"All apps\" or \"One app only\" to keep the stereo image."
                    : "Tip: \"All apps\" keeps working if you turn on Windows Mono audio.";
            else if (!Wasapi.ProcessLoopbackSupported)
                MonoText.Text = "This Windows version can't capture before the Mono downmix (needs Windows 10 2004 or newer).";
            else
                MonoText.Text = mono
                    ? "Windows Mono audio is ON: you hear mono, the bars still get true stereo."
                    : "Captured before Windows' Mono downmix, so turning Mono on won't flatten the bars.";

            // Auto sensitivity moves the value on its own; mirror it on the slider.
            if (_c.Profile.AutoSensitivity && !SensitivitySlider.IsMouseCaptureWithin)
            {
                _syncing = true;
                SensitivitySlider.Value = Math.Round(engine.Sensitivity);
                _syncing = false;
            }
        }

        // ---- events --------------------------------------------------------------------

        private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_syncing && ProfileCombo.SelectedIndex >= 0)
                _c.SelectProfile(ProfileCombo.SelectedIndex);
        }

        private void OnRenameProfile(object sender, RoutedEventArgs e)
        {
            var index = _c.Settings.CurrentProfile;
            var dialog = new RenameDialog(_c.ProfileDisplayName(index), $"Profile {index + 1}", Profile.MaxNameLength) { Owner = this };
            if (dialog.ShowDialog() == true)
                _c.RenameProfile(index, dialog.NewName == $"Profile {index + 1}" ? "" : dialog.NewName);
        }

        private void OnPowerClick(object sender, RoutedEventArgs e) => _c.RunAction("toggle_on_off");

        private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !(SourceCombo.SelectedItem is Choice choice))
                return;
            AppRow.Visibility = choice.Key == Sources.App ? Visibility.Visible : Visibility.Collapsed;
            if (choice.Key == Sources.App)
                RefreshApps();
            _c.ChangeSettings(s => s.Source = choice.Key);
        }

        private void OnAppCommitted(object sender, EventArgs e)
        {
            if (_syncing)
                return;
            var app = (AppCombo.Text ?? "").Trim();
            if (app != _c.Settings.SourceApp)
                _c.ChangeSettings(s => s.SourceApp = app);
        }

        private void OnAppKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                OnAppCommitted(sender, e);
        }

        private void OnRefreshApps(object sender, RoutedEventArgs e) => RefreshApps();

        private void RefreshApps()
        {
            var current = (AppCombo.Text ?? "").Trim();
            if (current.Length == 0)
                current = _c.Settings.SourceApp;
            var names = _c.ListAudioApps();
            if (current.Length > 0 && !names.Contains(current, StringComparer.OrdinalIgnoreCase))
                names.Insert(0, current);
            _syncing = true;
            AppCombo.ItemsSource = names;
            AppCombo.Text = current;
            _syncing = false;
        }

        private void OnSensitivityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateValueLabels();
            if (!_syncing)
                _c.ChangeProfile(p => p.Sensitivity = e.NewValue);
        }

        private void OnAutoSensitivityClick(object sender, RoutedEventArgs e)
        {
            var enabled = AutoSensitivityCheck.IsChecked == true;
            SensitivitySlider.IsEnabled = !enabled;
            _c.ChangeProfile(p => p.AutoSensitivity = enabled);
        }

        private void OnQuietBoostClick(object sender, RoutedEventArgs e) =>
            _c.ChangeProfile(p => p.QuietBoost = QuietBoostCheck.IsChecked == true);

        private void OnRadarClick(object sender, RoutedEventArgs e) =>
            _c.ChangeProfile(p => p.ShowDifference = RadarCheck.IsChecked == true);

        private void OnColorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !(ColorCombo.SelectedItem is SwatchChoice choice))
                return;
            _c.ChangeProfile(p => p.Color = choice.Name);
            var isFrequency = choice.Name == ColorSchemes.Frequency;
            PaletteLabel.Visibility = PaletteCombo.Visibility = isFrequency ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnPaletteChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !(PaletteCombo.SelectedItem is SwatchChoice choice))
                return;
            _c.ChangeProfile(p => p.FrequencyPalette = choice.Name);
            Sync(); // redraw the Frequency swatch in the color list
        }

        private void OnPickBackgroundColor(object sender, RoutedEventArgs e)
        {
            var current = Rgb.FromHex(_c.Profile.BackgroundColor);
            using (var dialog = new Forms.ColorDialog
            {
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
                FullOpen = true,
                AnyColor = true,
                CustomColors = _customColors ?? new int[0],
            })
            {
                if (dialog.ShowDialog(new Win32Owner(this)) != Forms.DialogResult.OK)
                    return;
                _customColors = dialog.CustomColors;
                var picked = new Rgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
                _c.ChangeProfile(p =>
                {
                    p.BackgroundColor = picked.ToHex();
                    if (p.BackgroundOpacity == 0)
                        p.BackgroundOpacity = 50; // picking a color at 0% would look like nothing happened
                });
                Sync();
            }
        }

        private void OnPrevColor(object sender, RoutedEventArgs e) => _c.RunAction("switch_color_left");

        private void OnNextColor(object sender, RoutedEventArgs e) => _c.RunAction("switch_color_right");

        private void OnModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_syncing && ModeCombo.SelectedItem is Choice choice)
                _c.ChangeProfile(p => p.Mode = choice.Key);
        }

        private void OnSegmentsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateValueLabels();
            if (!_syncing)
                _c.ChangeProfile(p => p.Segments = (int)e.NewValue);
        }

        private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateValueLabels();
            if (!_syncing)
                _c.ChangeProfile(p => p.Brightness = (int)e.NewValue);
        }

        private void OnBackgroundChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateValueLabels();
            if (!_syncing)
                _c.ChangeProfile(p => p.BackgroundOpacity = (int)e.NewValue);
        }

        private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_syncing && MonitorCombo.SelectedItem is Choice choice)
                _c.ChangeSettings(s => s.Monitor = choice.Key);
        }

        private void OnWidthChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateValueLabels();
            if (!_syncing)
                _c.ChangeSettings(s => s.BarWidth = (int)e.NewValue);
        }

        private void OnTopChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateValueLabels();
            if (!_syncing)
                _c.ChangeSettings(s => s.TopMargin = (int)e.NewValue);
        }

        private void OnBottomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateValueLabels();
            if (!_syncing)
                _c.ChangeSettings(s => s.BottomMargin = (int)e.NewValue);
        }

        private void OnMarginChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateValueLabels();
            if (!_syncing)
                _c.ChangeSettings(s => s.EdgeMargin = (int)e.NewValue);
        }

        private void OnHotkeys(object sender, RoutedEventArgs e)
        {
            var dialog = new HotkeyDialog(_c.Settings.Hotkeys) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;
            var rejected = _c.SetHotkeys(dialog.Mapping);
            if (rejected.Count > 0)
            {
                var labels = HotkeyActions.Labels.Where(l => rejected.Contains(l.Key)).Select(l => $"{l.Value}: {_c.Settings.Hotkeys[l.Key]}");
                MessageBox.Show(this, "These hotkeys couldn't be registered, probably because another app already uses them:\n\n" +
                    string.Join("\n", labels), "SoundRadar", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnRestartAudio(object sender, RoutedEventArgs e) => _c.Engine.Restart();

        private void OnAutostartClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Autostart.Set(AutostartCheck.IsChecked == true);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                AutostartCheck.IsChecked = Autostart.IsEnabled;
                MessageBox.Show(this, "Windows didn't allow changing the startup setting:\n" + ex.Message, "SoundRadar",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing the window just hides it; the bars keep running from the tray.
            if (!_c.IsShuttingDown)
            {
                e.Cancel = true;
                Hide();
                _c.NotifyHiddenToTray();
            }
            base.OnClosing(e);
        }
    }
}
