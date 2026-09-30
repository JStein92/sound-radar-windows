using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundRadar.Core;

namespace SoundRadar
{
    public partial class App : Application
    {
        // One running copy per settings folder. Normally that's the single %APPDATA% folder;
        // a SOUNDRADAR_CONFIG_DIR override (used by tests) gets its own set of names.
        private static readonly string InstanceSuffix = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SOUNDRADAR_CONFIG_DIR"))
            ? ""
            : "." + ConfigStore.Directory.ToLowerInvariant().GetHashCode().ToString("X8");
        private static readonly string InstanceMutexName = @"Local\SoundRadar.SingleInstance" + InstanceSuffix;
        private static readonly string ShowSignalName = @"Local\SoundRadar.ShowSettings" + InstanceSuffix;
        private static readonly string QuitSignalName = @"Local\SoundRadar.Quit" + InstanceSuffix;
        // A property, not a static field: "pack://" only parses once WPF's Application has initialized.
        private static Uri IconUri => new Uri("pack://application:,,,/Assets/icon.ico");

        private Mutex _instanceMutex;
        private EventWaitHandle _showSignal, _quitSignal;
        private RegisteredWaitHandle _showWait, _quitWait;
        private Controller _controller;

        internal static System.Drawing.Icon LoadIcon(int size)
        {
            using (var stream = GetResourceStream(IconUri).Stream)
                return new System.Drawing.Icon(stream, size, size);
        }

        internal static ImageSource LoadImageSource() => BitmapFrame.Create(IconUri);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandledException;
            bool HasArg(string name) => e.Args.Contains(name, StringComparer.OrdinalIgnoreCase);

            // The installer runs "--quit" before upgrading and "--uninstall" before removing files.
            if (HasArg("--quit") || HasArg("--uninstall"))
            {
                QuitRunningInstance();
                if (HasArg("--uninstall"))
                    Autostart.Set(false);
                Shutdown();
                return;
            }

            // One copy at a time: launching again (Start menu, taskbar) just reopens settings.
            _instanceMutex = new Mutex(true, InstanceMutexName, out var firstInstance);
            if (!firstInstance)
            {
                Signal(ShowSignalName);
                Shutdown();
                return;
            }
            Autostart.Refresh();

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName);
            _controller = new Controller(startHidden: e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase));
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
                (state, timedOut) => Dispatcher.BeginInvoke(new Action(() => _controller.ShowPanel())), null, Timeout.Infinite, false);
            _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitSignal,
                (state, timedOut) => Dispatcher.BeginInvoke(new Action(() => _controller.Quit())), null, Timeout.Infinite, true);
        }

        private static bool Signal(string name)
        {
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(name))
                    signal.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false; // nothing running (or it's still starting up)
            }
        }

        /// <summary>Ask the running copy to exit and wait (up to 10 s) until it has, so its files are free.</summary>
        private static void QuitRunningInstance()
        {
            if (!Signal(QuitSignalName))
                return;
            try
            {
                using (var mutex = Mutex.OpenExisting(InstanceMutexName))
                {
                    // The running copy owns the mutex until its process ends.
                    if (mutex.WaitOne(TimeSpan.FromSeconds(10)))
                        mutex.ReleaseMutex();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Already gone.
            }
            catch (AbandonedMutexException)
            {
                // It exited without releasing the mutex - which is what we were waiting for.
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _showWait?.Unregister(null);
            _quitWait?.Unregister(null);
            _controller?.Dispose();
            _showSignal?.Dispose();
            _quitSignal?.Dispose();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var log = Path.Combine(ConfigStore.Directory, "error.log");
            try
            {
                Directory.CreateDirectory(ConfigStore.Directory);
                File.AppendAllText(log, $"[{DateTime.Now:u}] {e.Exception}\r\n\r\n");
            }
            catch (IOException)
            {
                // Nothing more we can do.
            }
            MessageBox.Show($"SoundRadar hit an unexpected error and will keep running.\n\n{e.Exception.Message}\n\nDetails were saved to:\n{log}",
                "SoundRadar", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
