using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SoundRadar.Core;

namespace SoundRadar.Audio
{
    /// <summary>
    /// Captures audio on a worker thread and exposes the latest left/right levels.
    /// The UI sets the knobs directly and calls <see cref="TakeLevels"/> once per frame.
    /// </summary>
    internal sealed class AudioEngine
    {
        private const int AutoSensitivityWindow = 100; // blocks (~1 s of audio)
        private const double AutoSensitivityHeadroom = 0.9; // loudest recent peak lands at 90%
        private const double AppRescanSeconds = 3;
        private const double DecibelRange = 45;

        private readonly object _lock = new object();
        private readonly ManualResetEventSlim _restart = new ManualResetEventSlim(false);
        private readonly Queue<double> _autoWindow = new Queue<double>();
        private readonly Spectrum _spectrum = new Spectrum();
        private double _peakLeft, _peakRight, _freqLeft, _freqRight;
        private double _sensitivity = 25;
        private volatile bool _resetAuto;
        private volatile bool _running;
        private string _source = Sources.All;
        private string _sourceApp = "";
        private Thread _thread;

        public volatile bool AutoSensitivity;
        public volatile bool ShowDifference;
        public volatile bool QuietBoost;

        public string Status { get; private set; } = "Starting...";
        public bool StatusIsError { get; private set; }

        /// <summary>0-100 on a log curve, 25 = unity gain. Auto sensitivity moves it from the audio thread.</summary>
        public double Sensitivity
        {
            get => Volatile.Read(ref _sensitivity);
            set => Volatile.Write(ref _sensitivity, value);
        }

        /// <summary>Slider 0-100 -> gain 0.1x-1000x (25 = unity). Process loopback is pre-volume, so it must be able to attenuate.</summary>
        public static double SensitivityMultiplier(double sensitivity) => Math.Pow(10, (sensitivity - 25) / 25);

        public static double SensitivityForGain(double gain) => Math.Max(0, Math.Min(100, 25 + 25 * Math.Log10(gain)));

        /// <summary>Map 0-1 linear onto the top <see cref="DecibelRange"/> dB so quiet sounds still register.</summary>
        public static double DecibelScale(double level) => level <= 0 ? 0 : Math.Max(0, 1 + 20 * Math.Log10(level) / DecibelRange);

        public void Start()
        {
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "audio-capture" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _restart.Set();
            _thread?.Join(2000);
        }

        public void SetSource(string source, string app)
        {
            lock (_lock)
            {
                if (source == _source && app == _sourceApp)
                    return;
                _source = source;
                _sourceApp = app ?? "";
            }
            _restart.Set();
        }

        public void Restart() => _restart.Set();

        public void ResetAutoSensitivity() => _resetAuto = true;

        /// <summary>Loudest left/right level (0-1) since the previous call, plus the latest frequencies.</summary>
        public void TakeLevels(out double left, out double right, out double freqLeft, out double freqRight)
        {
            lock (_lock)
            {
                left = _peakLeft;
                right = _peakRight;
                freqLeft = _freqLeft;
                freqRight = _freqRight;
                _peakLeft = _peakRight = 0;
            }
        }

        private void SetStatus(string text, bool error = false)
        {
            Status = text;
            StatusIsError = error;
        }

        private void Run()
        {
            IMMDeviceEnumerator enumerator = null;
            while (_running)
            {
                _restart.Reset();
                try
                {
                    enumerator = enumerator ?? Wasapi.CreateEnumerator();
                    CaptureUntilRestart(enumerator);
                }
                catch (Exception ex) // device unplugged, driver hiccup, ...
                {
                    SetStatus("Audio error: " + ex.Message, true);
                    if (enumerator != null)
                        Marshal.ReleaseComObject(enumerator);
                    enumerator = null;
                    _restart.Wait(2000);
                }
            }
            if (enumerator != null)
                Marshal.ReleaseComObject(enumerator);
        }

        private static uint? FindAppPid(string exeName)
        {
            var match = Wasapi.ListAudioSessions()
                .Where(s => string.Equals(s.ExeName, exeName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Active) // prefer the session that's actually playing
                .Select(s => (uint?)s.Pid)
                .FirstOrDefault();
            return match;
        }

        private LoopbackCapture Open(IMMDeviceEnumerator enumerator, out uint? targetPid)
        {
            string source, app;
            lock (_lock)
            {
                source = _source;
                app = _sourceApp;
            }
            targetPid = null;

            if (source == Sources.App)
            {
                if (string.IsNullOrWhiteSpace(app))
                {
                    SetStatus("Pick an app to capture.", true);
                    return null;
                }
                targetPid = FindAppPid(app);
                if (targetPid == null)
                {
                    SetStatus($"Waiting for {app} to play audio...");
                    return null;
                }
                var appCapture = new LoopbackCapture(CaptureMode.IncludeProcess, enumerator, targetPid.Value);
                SetStatus($"Capturing {app} only - stereo");
                return appCapture;
            }

            if (source == Sources.Device || !Wasapi.ProcessLoopbackSupported)
            {
                var deviceCapture = new LoopbackCapture(CaptureMode.Endpoint, enumerator);
                SetStatus("Capturing output device" + (Wasapi.WindowsMonoEnabled() ? " - flattened if Windows Mono is on" : ""));
                return deviceCapture;
            }

            var ownPid = (uint)Process.GetCurrentProcess().Id;
            var capture = new LoopbackCapture(CaptureMode.ExcludeProcess, enumerator, ownPid);
            SetStatus("Capturing all apps - stereo");
            return capture;
        }

        private void CaptureUntilRestart(IMMDeviceEnumerator enumerator)
        {
            var capture = Open(enumerator, out var pid);
            if (capture == null)
            {
                _restart.Wait(1000);
                return;
            }
            using (capture)
            {
                var deviceId = Wasapi.DefaultRenderDeviceId(enumerator);
                var clock = Stopwatch.StartNew();
                var nextCheck = 1.0;
                var lastAudio = 0.0;
                var buffer = new float[4096];
                while (_running && !_restart.IsSet)
                {
                    var frames = capture.Read(ref buffer, 50);
                    var now = clock.Elapsed.TotalSeconds;
                    if (frames > 0)
                    {
                        lastAudio = now;
                        Analyze(buffer, frames, capture.Rate);
                    }
                    if (now < nextCheck)
                        continue;
                    nextCheck = now + 1;
                    if (Wasapi.DefaultRenderDeviceId(enumerator) != deviceId)
                        return; // output device switched - reopen on the new one
                    if (pid != null)
                    {
                        if (!Wasapi.ProcessAlive(pid.Value))
                            return;
                        // The app may have moved its audio to another process (launchers, browsers).
                        if (now - lastAudio > AppRescanSeconds)
                        {
                            lastAudio = now;
                            string app;
                            lock (_lock)
                                app = _sourceApp;
                            var newPid = FindAppPid(app);
                            if (newPid != null && newPid != pid)
                                return;
                        }
                    }
                }
            }
        }

        private void Analyze(float[] samples, int frames, int rate)
        {
            double rawLeft = 0, rawRight = 0;
            for (var i = 0; i < frames; i++)
            {
                var l = Math.Abs(samples[i * 2]);
                var r = Math.Abs(samples[i * 2 + 1]);
                if (l > rawLeft)
                    rawLeft = l;
                if (r > rawRight)
                    rawRight = r;
            }
            // Guard against garbage samples during device transitions.
            if (double.IsNaN(rawLeft) || double.IsInfinity(rawLeft))
                rawLeft = 0;
            if (double.IsNaN(rawRight) || double.IsInfinity(rawRight))
                rawRight = 0;
            var rawPeak = Math.Max(rawLeft, rawRight);

            if (_resetAuto)
            {
                _resetAuto = false;
                _autoWindow.Clear();
            }
            if (AutoSensitivity && rawPeak > 0)
            {
                // Aim the loudest peak of the last ~second just under full scale.
                _autoWindow.Enqueue(rawPeak);
                while (_autoWindow.Count > AutoSensitivityWindow)
                    _autoWindow.Dequeue();
                var windowMax = _autoWindow.Max();
                if (_autoWindow.Count >= 10 && windowMax > 0.003)
                {
                    var target = SensitivityForGain(AutoSensitivityHeadroom / windowMax);
                    Sensitivity = Sensitivity * 0.95 + target * 0.05;
                }
            }

            var gain = SensitivityMultiplier(Sensitivity);
            var levelLeft = Math.Min(1.0, rawLeft * gain);
            var levelRight = Math.Min(1.0, rawRight * gain);
            if (QuietBoost)
            {
                levelLeft = DecibelScale(levelLeft);
                levelRight = DecibelScale(levelRight);
            }
            if (ShowDifference)
            {
                // Radar: only the louder side lights, by how much louder it is.
                if (levelLeft > levelRight)
                {
                    levelLeft -= levelRight;
                    levelRight = 0;
                }
                else
                {
                    levelRight -= levelLeft;
                    levelLeft = 0;
                }
            }

            var freqLeft = _spectrum.AverageFrequency(samples, frames, 0, rate);
            var freqRight = _spectrum.AverageFrequency(samples, frames, 1, rate);

            lock (_lock)
            {
                _peakLeft = Math.Max(_peakLeft, levelLeft);
                _peakRight = Math.Max(_peakRight, levelRight);
                _freqLeft = freqLeft;
                _freqRight = freqRight;
            }
        }
    }
}
