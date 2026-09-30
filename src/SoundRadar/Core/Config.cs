using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SoundRadar.Core
{
    internal static class Sources
    {
        public const string All = "all"; // every app except SoundRadar, captured before the Mono downmix
        public const string App = "app"; // a single app, so voice chat or music doesn't move the bars
        public const string Device = "device"; // legacy endpoint loopback - flattened by Windows Mono
    }

    internal static class Modes
    {
        // How each bar fills: from the bottom up, out from the middle, or from the top down.
        public static readonly IReadOnlyList<string> All = new[] { "Bottom", "Center", "Top" };
    }

    internal static class HotkeyActions
    {
        public static readonly IReadOnlyList<KeyValuePair<string, string>> Labels = new[]
        {
            Pair("increase_brightness", "Brightness up"),
            Pair("decrease_brightness", "Brightness down"),
            Pair("increase_sensitivity", "Sensitivity up"),
            Pair("decrease_sensitivity", "Sensitivity down"),
            Pair("switch_mode_left", "Previous mode"),
            Pair("switch_mode_right", "Next mode"),
            Pair("switch_color_left", "Previous color"),
            Pair("switch_color_right", "Next color"),
            Pair("toggle_show_difference", "Toggle radar"),
            Pair("toggle_on_off", "Toggle on / off"),
            Pair("select_next_profile", "Next profile"),
            Pair("select_previous_profile", "Previous profile"),
        };

        /// <summary>All unbound by default so nothing fires while typing.</summary>
        public static Dictionary<string, string> Defaults() => Labels.ToDictionary(p => p.Key, _ => "");

        private static KeyValuePair<string, string> Pair(string action, string label) => new KeyValuePair<string, string>(action, label);
    }

    // Field names match the Python version's JSON, so existing settings carry over.

    [DataContract]
    internal sealed class Profile
    {
        [DataMember(Name = "mode")] public string Mode;
        [DataMember(Name = "color")] public string Color;
        [DataMember(Name = "show_difference")] public bool ShowDifference;
        [DataMember(Name = "brightness")] public int Brightness; // bar opacity, percent
        [DataMember(Name = "background_opacity")] public int BackgroundOpacity; // backing behind each bar, percent
        [DataMember(Name = "background_color")] public string BackgroundColor; // "#RRGGBB"
        [DataMember(Name = "sensitivity")] public double Sensitivity; // 0-100 log curve, 25 = unity
        [DataMember(Name = "auto_sensitivity")] public bool AutoSensitivity;
        [DataMember(Name = "quiet_boost")] public bool QuietBoost; // decibel scale so footsteps still show
        [DataMember(Name = "segments")] public int Segments; // 0 = smooth bar
        [DataMember(Name = "frequency_palette")] public string FrequencyPalette; // used by the Frequency color
        [DataMember(Name = "name")] public string Name; // "" = "Profile N"

        public const int MaxNameLength = 40;

        public Profile() => SetDefaults();

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) => SetDefaults();

        private void SetDefaults()
        {
            Mode = "Bottom";
            Color = "Rainbow";
            ShowDifference = false;
            Brightness = 90;
            BackgroundOpacity = 0;
            BackgroundColor = "#000000";
            Sensitivity = 25;
            AutoSensitivity = true;
            QuietBoost = false;
            Segments = 21;
            FrequencyPalette = FrequencyPalettes.Names[0];
            Name = "";
        }

        public void Normalize()
        {
            if (!Modes.All.Contains(Mode))
                Mode = Modes.All[0];
            if (!ColorSchemes.Names.Contains(Color))
                Color = ColorSchemes.Names[0];
            if (!FrequencyPalettes.Names.Contains(FrequencyPalette))
                FrequencyPalette = FrequencyPalettes.Names[0];
            BackgroundColor = Rgb.TryParseHex(BackgroundColor, out var background) ? background.ToHex() : "#000000";
            Brightness = Math.Max(5, Math.Min(100, Brightness));
            BackgroundOpacity = Math.Max(0, Math.Min(100, BackgroundOpacity));
            Sensitivity = Math.Max(0, Math.Min(100, Sensitivity));
            Segments = Math.Max(0, Math.Min(80, Segments));
            Name = (Name ?? "").Trim();
            if (Name.Length > MaxNameLength)
                Name = Name.Substring(0, MaxNameLength);
        }
    }

    [DataContract]
    internal sealed class AppSettings
    {
        public const string AllMonitors = "*all*"; // left bar on the leftmost display, right bar on the rightmost

        [DataMember(Name = "current_profile")] public int CurrentProfile;
        [DataMember(Name = "enabled")] public bool Enabled;
        [DataMember(Name = "source")] public string Source;
        [DataMember(Name = "source_app")] public string SourceApp; // exe name, e.g. "cs2.exe"
        [DataMember(Name = "monitor")] public string Monitor; // display device name, AllMonitors, or "" = primary
        [DataMember(Name = "bar_width")] public int BarWidth; // px at 100% scaling
        [DataMember(Name = "top_margin")] public int TopMargin; // px from the top of the screen at 100% scaling
        [DataMember(Name = "bottom_margin")] public int BottomMargin; // px from the bottom
        [DataMember(Name = "edge_margin")] public int EdgeMargin; // px from the side of the screen
        [DataMember(Name = "hotkeys")] public Dictionary<string, string> Hotkeys;

        // Older versions stored a single centered length as a percent of screen height. Read
        // it once to seed the top/bottom gaps (see MigrateLength); never written back.
        [DataMember(Name = "bar_length", EmitDefaultValue = false)] public int LegacyBarLength;

        public const int MaxVerticalMargin = 800;
        public const int Unset = -1;

        public AppSettings() => SetDefaults();

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) => SetDefaults();

        private void SetDefaults()
        {
            CurrentProfile = 0;
            Enabled = true;
            Source = Sources.All;
            SourceApp = "";
            Monitor = "";
            BarWidth = 12;
            TopMargin = Unset;
            BottomMargin = Unset;
            LegacyBarLength = 0;
            EdgeMargin = 0;
            Hotkeys = HotkeyActions.Defaults();
        }

        /// <summary>
        /// Fill in gaps that were never set: from an old centered length if there is one,
        /// otherwise a sensible default. Needs the (100%-scale) height of the primary screen.
        /// </summary>
        public void MigrateLength(int screenHeight)
        {
            if (TopMargin != Unset && BottomMargin != Unset)
                return;
            var gap = LegacyBarLength > 0
                ? (int)Math.Round(screenHeight * (100 - Math.Max(10, Math.Min(100, LegacyBarLength))) / 200.0)
                : 180;
            if (TopMargin == Unset)
                TopMargin = gap;
            if (BottomMargin == Unset)
                BottomMargin = gap;
            LegacyBarLength = 0;
            Normalize();
        }

        public void Normalize()
        {
            CurrentProfile = Math.Max(0, Math.Min(ConfigStore.ProfileCount - 1, CurrentProfile));
            if (Source != Sources.All && Source != Sources.App && Source != Sources.Device)
                Source = Sources.All;
            SourceApp = SourceApp ?? "";
            Monitor = Monitor ?? "";
            BarWidth = Math.Max(2, Math.Min(80, BarWidth));
            if (TopMargin != Unset)
                TopMargin = Math.Max(0, Math.Min(MaxVerticalMargin, TopMargin));
            if (BottomMargin != Unset)
                BottomMargin = Math.Max(0, Math.Min(MaxVerticalMargin, BottomMargin));
            EdgeMargin = Math.Max(0, Math.Min(300, EdgeMargin));
            // Pick up actions added after the file was written. Hotkeys from the Python
            // version use a different syntax; ones that don't parse are simply ignored.
            var merged = HotkeyActions.Defaults();
            foreach (var pair in Hotkeys ?? new Dictionary<string, string>())
                if (merged.ContainsKey(pair.Key))
                    merged[pair.Key] = pair.Value ?? "";
            Hotkeys = merged;
        }
    }

    /// <summary>Profiles and settings as JSON under %APPDATA%\SoundRadarDesktop.</summary>
    internal static class ConfigStore
    {
        public const int ProfileCount = 9;

        public static readonly string Directory = Environment.GetEnvironmentVariable("SOUNDRADAR_CONFIG_DIR") is string dir && dir.Length > 0
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundRadarDesktop");

        private static string ProfilePath(int index) => Path.Combine(Directory, $"profile-{index}.json");

        private static string SettingsPath => Path.Combine(Directory, "settings.json");

        public static Profile LoadProfile(int index)
        {
            var profile = Load<Profile>(ProfilePath(index)) ?? new Profile();
            profile.Normalize();
            return profile;
        }

        public static void SaveProfile(int index, Profile profile) => Save(ProfilePath(index), profile);

        public static AppSettings LoadSettings()
        {
            var settings = Load<AppSettings>(SettingsPath) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }

        public static void SaveSettings(AppSettings settings) => Save(SettingsPath, settings);

        private static DataContractJsonSerializer Serializer<T>() =>
            new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        private static T Load<T>(string path) where T : class
        {
            try
            {
                using (var stream = File.OpenRead(path))
                    return (T)Serializer<T>().ReadObject(stream);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException)
            {
                return null; // missing or unreadable: fall back to defaults
            }
        }

        private static void Save<T>(string path, T value)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true, "  "))
                Serializer<T>().WriteObject(writer, value);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
    }
}
