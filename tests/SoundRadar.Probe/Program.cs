using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SoundRadar.Audio;
using SoundRadar.Core;

namespace SoundRadar.Probe
{
    /// <summary>
    /// Plays a left-only test tone from a separate process and checks what each capture
    /// mode sees, plus a few pure-logic checks. Usage: SoundRadar.Probe [audio|logic|all]
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        [STAThread]
        private static int Main(string[] args)
        {
            var what = args.FirstOrDefault() ?? "all";
            var temp = Path.Combine(Path.GetTempPath(), "SoundRadarProbe");
            Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("SOUNDRADAR_CONFIG_DIR", Path.Combine(temp, "config"));

            if (what == "logic" || what == "all")
                LogicChecks(temp);
            if (what == "audio" || what == "all")
                AudioChecks(temp);

            Console.WriteLine(_failures == 0 ? "\nALL CHECKS PASSED" : $"\n{_failures} CHECK(S) FAILED");
            return _failures;
        }

        private static void Check(bool ok, string description)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {description}");
            if (!ok)
                _failures++;
        }

        // ---- logic -------------------------------------------------------------------------

        private static void LogicChecks(string temp)
        {
            Console.WriteLine("Hotkeys");
            Check(HotkeyText.TryParse("Ctrl+Shift+F9", out var mods, out var vk) && mods == 0x6 && vk == 0x78, "Ctrl+Shift+F9 -> MOD_CONTROL|MOD_SHIFT, VK_F9");
            Check(HotkeyText.TryParse("Ctrl+=", out mods, out vk) && mods == 0x2 && vk == 0xBB, "Ctrl+= -> VK_OEM_PLUS");
            Check(HotkeyText.TryParse("Alt+1", out mods, out vk) && mods == 0x1 && vk == 0x31, "Alt+1 -> '1'");
            Check(!HotkeyText.TryParse("ctrl+]+[", out _, out _), "two keys rejected");
            Check(!HotkeyText.TryParse("ctrl+", out _, out _), "modifier only rejected");
            Check(!HotkeyText.TryParse("", out _, out _), "empty rejected");
            var formatted = HotkeyText.Format(System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt,
                System.Windows.Input.Key.OemCloseBrackets);
            Check(formatted == "Ctrl+Alt+]" && HotkeyText.TryParse(formatted, out _, out vk) && vk == 0xDD, $"format/parse round trip ({formatted})");

            Console.WriteLine("Spectrum");
            var spectrum = new Spectrum();
            foreach (var hz in new[] { 220.0, 440.0, 1000.0 })
            {
                var samples = new float[480 * 2];
                for (var i = 0; i < 480; i++)
                    samples[i * 2] = (float)Math.Sin(2 * Math.PI * hz * i / 48000);
                var measured = spectrum.AverageFrequency(samples, 480, 0, 48000);
                Check(Math.Abs(measured - hz) < hz * 0.35 + 60, $"{hz} Hz sine -> {measured:0} Hz");
            }

            Console.WriteLine("Sensitivity curve");
            Check(Math.Abs(AudioEngine.SensitivityMultiplier(25) - 1) < 1e-9, "25 = unity gain");
            Check(Math.Abs(AudioEngine.SensitivityForGain(AudioEngine.SensitivityMultiplier(63)) - 63) < 1e-6, "gain <-> sensitivity round trip");
            Check(Math.Abs(AudioEngine.DecibelScale(1) - 1) < 1e-9 && AudioEngine.DecibelScale(0.0056) < 0.01, "decibel scale spans ~45 dB");

            Console.WriteLine("Colors");
            var trans = ColorSchemes.Get("Transgender");
            Check(trans.IsFlag && trans.Gradient(0).ToHex() == "#5BCEFA" && trans.Gradient(128).ToHex() == "#FFFFFF" && trans.Gradient(255).ToHex() == "#5BCEFA",
                "Transgender: blue / pink / white / pink / blue");
            var pride = ColorSchemes.Get("Rainbow Pride");
            Check(pride.Gradient(0).ToHex() == "#750787" && pride.Gradient(255).ToHex() == "#E40303",
                "Rainbow Pride: violet at the start of the fill, red at the top (reads like the flag)");
            var stops = ColorSchemes.BarStops(pride, 16);
            Check(stops.Count == 12 && stops.Zip(stops.Skip(1), (a, b) => b.t > a.t).All(x => x) && stops[0].t == 0 && Math.Abs(stops[stops.Count - 1].t - 1) < 1e-9,
                "flag gradient stops are hard-edged and strictly increasing (GDI+ requirement)");
            Check(ColorSchemes.Names.Distinct().Count() == ColorSchemes.Names.Count && ColorSchemes.Names.Count == 19, $"{ColorSchemes.Names.Count} uniquely named schemes");
            Check(Rgb.TryParseHex("#1a2B3c", out var parsed) && parsed.ToHex() == "#1A2B3C" && !Rgb.TryParseHex("red", out _), "hex colors parse and format");

            Console.WriteLine("Frequency palettes");
            foreach (var palette in FrequencyPalettes.All)
                Console.WriteLine($"    {palette.Name,-13} 50 Hz {palette.ColorFor(50).ToHex()}  300 Hz {palette.ColorFor(300).ToHex()}  2 kHz {palette.ColorFor(2000).ToHex()}");
            Check(FrequencyPalettes.Get("Classic").ColorFor(50).ToHex() == "#FF0400" && FrequencyPalettes.Get("Classic").ColorFor(2000).ToHex() == "#DD00DD",
                "Classic matches the LED sketch (red low, purple high)");
            Check(FrequencyPalettes.Get("Spectrum").ColorFor(50).ToHex() == "#FF0000", "Spectrum starts red");
            Check(FrequencyPalettes.Get("Heat").ColorFor(9999).ToHex() == "#FFFFFF" && FrequencyPalettes.Get("Heat").ColorFor(1).ToHex() == "#7A0000",
                "out-of-range frequencies clamp to the palette ends");

            Console.WriteLine("Placement migration");
            var legacy = new AppSettings { LegacyBarLength = 94, TopMargin = AppSettings.Unset, BottomMargin = AppSettings.Unset };
            legacy.MigrateLength(1440);
            Check(legacy.TopMargin == 43 && legacy.BottomMargin == 43 && legacy.LegacyBarLength == 0, $"old 94% length on 1440 px -> top/bottom {legacy.TopMargin}/{legacy.BottomMargin} px");
            var brandNew = new AppSettings();
            brandNew.MigrateLength(1080);
            Check(brandNew.TopMargin == 180 && brandNew.BottomMargin == 180, "fresh install defaults to 180 px gaps");
            var kept = new AppSettings { TopMargin = 10, BottomMargin = 60 };
            kept.MigrateLength(1440);
            Check(kept.TopMargin == 10 && kept.BottomMargin == 60, "existing gaps are left alone");

            Console.WriteLine("Config (a copy of your real settings)");
            var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundRadarDesktop");
            var copy = Environment.GetEnvironmentVariable("SOUNDRADAR_CONFIG_DIR");
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.Exists(real) ? Directory.GetFiles(real, "*.json") : new string[0])
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), true);
            var settings = ConfigStore.LoadSettings();
            var profile = ConfigStore.LoadProfile(settings.CurrentProfile);
            settings.MigrateLength(1440);
            Console.WriteLine($"    settings: source={settings.Source} monitor='{settings.Monitor}' width={settings.BarWidth} top={settings.TopMargin} bottom={settings.BottomMargin} side={settings.EdgeMargin} hotkeys={settings.Hotkeys.Count(h => h.Value != "")}/{settings.Hotkeys.Count} bound");
            Console.WriteLine($"    profile {settings.CurrentProfile}: color={profile.Color} mode={profile.Mode} brightness={profile.Brightness} bg={profile.BackgroundOpacity} sens={profile.Sensitivity:0.0} auto={profile.AutoSensitivity} segments={profile.Segments}");
            Check(settings.Hotkeys.Count == HotkeyActions.Labels.Count, "all hotkey actions present after load");
            profile.Name = "  Valorant  ";
            profile.FrequencyPalette = "Ocean";
            profile.BackgroundColor = "#123abc";
            profile.Normalize();
            ConfigStore.SaveProfile(8, profile);
            var reloaded = ConfigStore.LoadProfile(8);
            Check(reloaded.Color == profile.Color && reloaded.Brightness == profile.Brightness && reloaded.Segments == profile.Segments
                  && reloaded.Name == "Valorant" && reloaded.FrequencyPalette == "Ocean" && reloaded.BackgroundColor == "#123ABC",
                "profile save/load round trip (incl. name, palette, background color)");
            ConfigStore.SaveSettings(settings);
            var settingsJson = File.ReadAllText(Path.Combine(copy, "settings.json"));
            Check(settingsJson.Contains("top_margin") && !settingsJson.Contains("bar_length"), "settings save gaps and drop the old length");
            File.WriteAllText(Path.Combine(copy, "profile-5.json"), "{\"background_color\": \"not a color\", \"frequency_palette\": \"Nope\"}");
            var bad = ConfigStore.LoadProfile(5);
            Check(bad.BackgroundColor == "#000000" && bad.FrequencyPalette == "Classic", "invalid color/palette fall back to defaults");
            var fresh = ConfigStore.LoadProfile(7); // never written
            Check(fresh.Brightness == 90 && fresh.AutoSensitivity && fresh.Segments == 21, "missing profile falls back to defaults");
            File.WriteAllText(Path.Combine(copy, "profile-6.json"), "{\"color\": \"Blue\"}");
            var partial = ConfigStore.LoadProfile(6);
            Check(partial.Color == "Blue" && partial.Brightness == 90, "partial file keeps defaults for missing fields");
        }

        // ---- audio -------------------------------------------------------------------------

        private static void AudioChecks(string temp)
        {
            Console.WriteLine($"Audio (Windows build {Wasapi.WindowsBuild}, process loopback {(Wasapi.ProcessLoopbackSupported ? "supported" : "NOT supported")}, Windows Mono {(Wasapi.WindowsMonoEnabled() ? "ON" : "off")})");
            var wav = Path.Combine(temp, "left_only.wav");
            WriteLeftOnlyTone(wav, seconds: 3);

            var all = Measure(Sources.All, "", wav);
            Check(all.left > 0.02 && all.left > all.right * 1.5, $"All apps: left louder than right (L={all.left:0.000} R={all.right:0.000}, other audio may add to both)");

            var app = Measure(Sources.App, "powershell.exe", wav);
            Check(app.left > 0.02 && app.right < 0.005, $"One app (powershell.exe): left only (L={app.left:0.000} R={app.right:0.000}) status='{app.status}'");

            var device = Measure(Sources.Device, "", wav);
            var mono = Wasapi.WindowsMonoEnabled();
            Check(device.left > 0.001, $"Output device captures audio (L={device.left:0.000} R={device.right:0.000}){(mono ? " - identical L/R expected with Mono on" : "")}");
            if (mono)
                Check(Math.Abs(device.left - device.right) < 0.01, "Output device is flattened by Windows Mono (why the new capture exists)");

            var radar = Measure(Sources.App, "powershell.exe", wav, showDifference: true);
            Check(radar.left > 0.02 && radar.right == 0, $"Radar: only the louder (left) side lights (L={radar.left:0.000} R={radar.right:0.000})");

            Console.WriteLine($"    frequency seen for the 440 Hz tone: {app.freq:0} Hz");
        }

        private static (double left, double right, double freq, string status) Measure(string source, string app, string wav, bool showDifference = false)
        {
            var engine = new AudioEngine { Sensitivity = 25, ShowDifference = showDifference };
            engine.SetSource(source, app);
            engine.Start();
            // "All apps" excludes SoundRadar's whole process tree, so the player must not be our
            // child: launch it through WMI, which parents it to the WMI service instead.
            var command = $"powershell.exe -NoProfile -WindowStyle Hidden -Command \"(New-Object Media.SoundPlayer '{wav}').PlaySync()\"";
            var player = StartOutsideOurTree(command);
            Thread.Sleep(source == Sources.App ? 1500 : 700); // app mode has to find the player's session first
            var lefts = new List<double>();
            var rights = new List<double>();
            double freq = 0;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 1.2)
            {
                engine.TakeLevels(out var l, out var r, out var fl, out _);
                lefts.Add(l);
                rights.Add(r);
                if (fl > 0)
                    freq = fl;
                Thread.Sleep(16);
            }
            var status = engine.Status;
            engine.Stop();
            player?.WaitForExit();
            return (lefts.Average(), rights.Average(), freq, status);
        }

        private static Process StartOutsideOurTree(string commandLine)
        {
            using (var processClass = new System.Management.ManagementClass("Win32_Process"))
            using (var inParams = processClass.GetMethodParameters("Create"))
            {
                inParams["CommandLine"] = commandLine;
                using (var result = processClass.InvokeMethod("Create", inParams, null))
                {
                    var pid = Convert.ToInt32(result["ProcessId"]);
                    try
                    {
                        return Process.GetProcessById(pid);
                    }
                    catch (ArgumentException)
                    {
                        return null; // already finished
                    }
                }
            }
        }

        private static void WriteLeftOnlyTone(string path, int seconds)
        {
            const int rate = 48000;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                var frames = rate * seconds;
                var dataBytes = frames * 4;
                writer.Write("RIFF".ToCharArray());
                writer.Write(36 + dataBytes);
                writer.Write("WAVEfmt ".ToCharArray());
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)2);
                writer.Write(rate);
                writer.Write(rate * 4);
                writer.Write((short)4);
                writer.Write((short)16);
                writer.Write("data".ToCharArray());
                writer.Write(dataBytes);
                for (var i = 0; i < frames; i++)
                {
                    writer.Write((short)(0.08 * Math.Sin(2 * Math.PI * 440 * i / rate) * short.MaxValue));
                    writer.Write((short)0);
                }
            }
        }
    }
}
