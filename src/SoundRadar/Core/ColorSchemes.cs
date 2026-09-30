using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace SoundRadar.Core
{
    internal readonly struct Rgb
    {
        public Rgb(int r, int g, int b)
        {
            R = (byte)Math.Max(0, Math.Min(255, r));
            G = (byte)Math.Max(0, Math.Min(255, g));
            B = (byte)Math.Max(0, Math.Min(255, b));
        }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

        public static bool TryParseHex(string text, out Rgb color)
        {
            color = default;
            text = (text ?? "").Trim().TrimStart('#');
            if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return false;
            color = new Rgb(value >> 16 & 0xFF, value >> 8 & 0xFF, value & 0xFF);
            return true;
        }

        public static Rgb FromHex(string text) => TryParseHex(text, out var color) ? color : new Rgb(0, 0, 0);

        public static Rgb Lerp(Rgb a, Rgb b, double t) =>
            new Rgb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>
    /// A color scheme maps a position 0-255 along the bar (0 = where it starts filling) to a
    /// color. Flag schemes also keep their stripes (top to bottom, as the flag is drawn) so
    /// bars and swatches can render crisp bands instead of blending them.
    /// </summary>
    internal sealed class ColorScheme
    {
        public ColorScheme(string name, Func<int, Rgb> gradient, IReadOnlyList<Rgb> stripes = null)
        {
            Name = name;
            Gradient = gradient;
            Stripes = stripes;
        }

        public string Name { get; }
        /// <summary>Null for the Frequency scheme (a solid, frequency-driven color).</summary>
        public Func<int, Rgb> Gradient { get; }
        public IReadOnlyList<Rgb> Stripes { get; }
        public bool IsFlag => Stripes != null;
    }

    internal static class ColorSchemes
    {
        public const string Frequency = "Frequency";

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static readonly IReadOnlyList<ColorScheme> All = new[]
        {
            // The original LED-strip schemes, ported from the Arduino sketch.
            new ColorScheme("Rainbow", Rainbow),
            new ColorScheme("Green", pos => new Rgb(50 + pos / 2, 255 - pos / 2, 0)),
            new ColorScheme("Blue", pos => new Rgb(150 * pos / 255, 40, 255 - pos / 6)),
            new ColorScheme("Orange", pos => new Rgb(255 - pos / 4, 50 + 205 * pos / 255, 0)),
            new ColorScheme("Purple", pos => new Rgb(255 - pos, 0, 255)),
            new ColorScheme("Reverse Rainbow", ReverseRainbow),
            new ColorScheme(Frequency, null),
            new ColorScheme("Fade", _ => Wheel((int)(Clock.ElapsedMilliseconds / 128 % 256))), // ~33 s per lap
            new ColorScheme("White", _ => new Rgb(255, 255, 255)),

            // Pride flags, stripes listed top to bottom.
            Flag("Rainbow Pride", "#E40303", "#FF8C00", "#FFED00", "#008026", "#004DFF", "#750787"),
            Flag("Transgender", "#5BCEFA", "#F5A9B8", "#FFFFFF", "#F5A9B8", "#5BCEFA"),
            Flag("Bisexual", "#D60270", "#D60270", "#9B4F96", "#0038A8", "#0038A8"), // 2:1:2
            Flag("Pansexual", "#FF218C", "#FFD800", "#21B1FF"),
            Flag("Lesbian", "#D52D00", "#EF7627", "#FF9A56", "#FFFFFF", "#D162A4", "#B55690", "#A30262"),
            Flag("Nonbinary", "#FCF434", "#FFFFFF", "#9C59D1", "#2C2C2C"),
            Flag("Asexual", "#000000", "#A3A3A3", "#FFFFFF", "#800080"),
            Flag("Aromantic", "#3DA542", "#A7D379", "#FFFFFF", "#A9A9A9", "#000000"),
            Flag("Genderfluid", "#FF76A4", "#FFFFFF", "#C011D7", "#000000", "#2F3CBE"),
            Flag("Agender", "#000000", "#BCC4C7", "#FFFFFF", "#B7F684", "#FFFFFF", "#BCC4C7", "#000000"),
        };

        public static readonly IReadOnlyList<string> Names = All.Select(s => s.Name).ToList();

        public static ColorScheme Get(string name) => All.FirstOrDefault(s => s.Name == name) ?? All[0];

        private static ColorScheme Flag(string name, params string[] stripesTopToBottom)
        {
            var stripes = stripesTopToBottom.Select(Rgb.FromHex).ToArray();
            // Position 0 is the start of the fill (the bottom of a bar filling upward), so it
            // gets the bottom stripe and a full bar reads like the flag.
            return new ColorScheme(name, pos => stripes[stripes.Length - 1 - Math.Min(stripes.Length - 1, pos * stripes.Length / 256)], stripes);
        }

        private static Rgb Rainbow(int pos)
        {
            if (pos < 64) return new Rgb(255 - pos * 4, pos * 4, 0); // red -> green
            if (pos < 128) return new Rgb(0, 255 - (pos - 64) * 4, (pos - 64) * 4); // green -> blue
            if (pos < 192) return new Rgb((pos - 128) * 4, 0, 255 - (pos - 128) * 2); // blue -> purple
            return new Rgb(255, 0, 255);
        }

        private static Rgb ReverseRainbow(int pos)
        {
            if (pos < 64) return new Rgb(255 - pos * 4, 0, 255 - pos); // purple -> blue
            if (pos < 128) return new Rgb(0, (pos - 64) * 4, 255 - (pos - 64) * 4); // blue -> green
            if (pos < 192) return new Rgb((pos - 128) * 4, 255 - (pos - 128) * 2, 0); // green -> red
            return new Rgb(255, 0, 0);
        }

        /// <summary>Red -> green -> blue -> red.</summary>
        public static Rgb Wheel(int pos)
        {
            if (pos < 85) return new Rgb(255 - pos * 3, pos * 3, 0);
            if (pos < 170) return new Rgb(0, 255 - (pos - 85) * 3, (pos - 85) * 3);
            return new Rgb((pos - 170) * 3, 0, 255 - (pos - 170) * 3);
        }

        /// <summary>
        /// Gradient stops along the bar (t = 0 at the start of the fill). Flags get hard-edged
        /// stops so smooth bars show crisp stripes.
        /// </summary>
        public static List<(double t, Rgb color)> BarStops(ColorScheme scheme, int samples)
        {
            var stops = new List<(double, Rgb)>();
            if (scheme.IsFlag)
            {
                var n = scheme.Stripes.Count;
                for (var i = 0; i < n; i++)
                {
                    var color = scheme.Stripes[n - 1 - i]; // bottom stripe first
                    stops.Add((i / (double)n + (i == 0 ? 0 : 0.0005), color));
                    stops.Add(((i + 1) / (double)n, color));
                }
                return stops;
            }
            for (var k = 0; k <= samples; k++)
                stops.Add((k / (double)samples, scheme.Gradient(k * 255 / samples)));
            return stops;
        }

        /// <summary>
        /// Stops for a horizontal preview swatch. Flags read top stripe first (left to right),
        /// Fade shows its wheel, Frequency shows the chosen palette's range.
        /// </summary>
        public static List<(double t, Rgb color)> SwatchStops(ColorScheme scheme, FrequencyPalette palette = null)
        {
            const int samples = 24;
            var stops = new List<(double, Rgb)>();
            if (scheme.IsFlag)
            {
                var n = scheme.Stripes.Count;
                for (var i = 0; i < n; i++)
                {
                    stops.Add((i / (double)n + (i == 0 ? 0 : 0.0005), scheme.Stripes[i]));
                    stops.Add(((i + 1) / (double)n, scheme.Stripes[i]));
                }
                return stops;
            }
            for (var k = 0; k <= samples; k++)
            {
                var t = k / (double)samples;
                Rgb color;
                if (scheme.Name == Frequency)
                    color = (palette ?? FrequencyPalettes.All[0]).ColorAt(t);
                else if (scheme.Name == "Fade")
                    color = Wheel(k * 255 / samples);
                else
                    color = scheme.Gradient(k * 255 / samples);
                stops.Add((t, color));
            }
            return stops;
        }
    }

    /// <summary>How the Frequency scheme colors a bar from its dominant frequency.</summary>
    internal sealed class FrequencyPalette
    {
        private readonly Func<double, Rgb> _byPosition;

        public FrequencyPalette(string name, Func<double, Rgb> byPosition, Func<double, double> position)
        {
            Name = name;
            _byPosition = byPosition;
            Position = position;
        }

        public string Name { get; }
        /// <summary>Maps a frequency in Hz to 0 (lowest) - 1 (highest).</summary>
        public Func<double, double> Position { get; }

        public Rgb ColorAt(double t) => _byPosition(Math.Max(0, Math.Min(1, t)));
        public Rgb ColorFor(double hz) => ColorAt(Position(hz));
    }

    internal static class FrequencyPalettes
    {
        public const double MinHz = 50;
        public const double MaxHz = 2000;

        // The sketch's bands (0-255 after mapping MinHz..MaxHz linearly). It capped them at 115
        // for LED current limits; on screen they're scaled back up to full intensity.
        private static readonly (int limit, Rgb color)[] ClassicBands =
        {
            (25, new Rgb(255, 4, 0)), // red (lowest)
            (60, new Rgb(255, 84, 0)), // red-orange
            (100, new Rgb(255, 161, 0)), // orange
            (150, new Rgb(255, 255, 11)), // yellow
            (190, new Rgb(0, 255, 11)), // green
            (210, new Rgb(11, 0, 255)), // blue
            (255, new Rgb(221, 0, 221)), // purple (highest)
        };

        public static readonly IReadOnlyList<FrequencyPalette> All = new[]
        {
            new FrequencyPalette("Classic", t => ClassicBands.First(b => t * 255 <= b.limit).color, Linear),
            new FrequencyPalette("Spectrum", t => Hue(t * 280), Logarithmic), // red (bass) -> violet (treble)
            Blend("Heat", "#7A0000", "#E81E00", "#FF8C00", "#FFE000", "#FFFFFF"),
            Blend("Ocean", "#0B2E8C", "#1E6FFF", "#00C8D7", "#B8FFF6"),
            Blend("Cool to warm", "#2C7BB6", "#ABD9E9", "#FFFFBF", "#FDAE61", "#D7191C"),
            Blend("Neon", "#FF00E6", "#8A2BFF", "#00F0FF", "#39FF14"),
        };

        public static readonly IReadOnlyList<string> Names = All.Select(p => p.Name).ToList();

        public static FrequencyPalette Get(string name) => All.FirstOrDefault(p => p.Name == name) ?? All[0];

        private static double Clamp(double hz) => Math.Max(MinHz, Math.Min(MaxHz, hz));

        private static double Linear(double hz) => (Clamp(hz) - MinHz) / (MaxHz - MinHz);

        // Pitch is logarithmic, so this spreads bass, mids and treble evenly across the palette.
        private static double Logarithmic(double hz) => Math.Log(Clamp(hz) / MinHz) / Math.Log(MaxHz / MinHz);

        private static FrequencyPalette Blend(string name, params string[] hexStops)
        {
            var colors = hexStops.Select(Rgb.FromHex).ToArray();
            return new FrequencyPalette(name, t =>
            {
                var scaled = t * (colors.Length - 1);
                var i = Math.Min(colors.Length - 2, (int)scaled);
                return Rgb.Lerp(colors[i], colors[i + 1], scaled - i);
            }, Logarithmic);
        }

        private static Rgb Hue(double degrees)
        {
            var h = degrees / 60;
            var x = 1 - Math.Abs(h % 2 - 1);
            double r = 0, g = 0, b = 0;
            if (h < 1) { r = 1; g = x; }
            else if (h < 2) { r = x; g = 1; }
            else if (h < 3) { g = 1; b = x; }
            else if (h < 4) { g = x; b = 1; }
            else if (h < 5) { r = x; b = 1; }
            else { r = 1; b = x; }
            return new Rgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }
    }

    /// <summary>Eases toward the color for the current dominant frequency.</summary>
    internal sealed class FrequencyColor
    {
        private const double StepPerSecond = 5 * (255.0 / 115) * 86; // sketch: 5 per packet at ~86 packets/s
        private double _r, _g, _b;

        public Rgb Update(double hz, double dt, FrequencyPalette palette)
        {
            var target = palette.ColorFor(hz);
            var step = StepPerSecond * dt;
            _r += Clamp(target.R - _r, step);
            _g += Clamp(target.G - _g, step);
            _b += Clamp(target.B - _b, step);
            return new Rgb((int)_r, (int)_g, (int)_b);
        }

        private static double Clamp(double delta, double step) => Math.Max(-step, Math.Min(step, delta));
    }
}
