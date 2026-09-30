using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    }

    /// <summary>
    /// Color schemes, ported from the SoundRadar Arduino sketch. Each gradient maps a
    /// position 0-255 along the bar (0 = where it starts filling) to a color.
    /// </summary>
    internal static class ColorSchemes
    {
        public const string Frequency = "Frequency";
        public const int FreqMin = 50;
        public const int FreqMax = 2000;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        // Frequency bands (0-255 after mapping FreqMin..FreqMax). The sketch capped these at
        // 115 for LED current limits; on screen they're scaled back up to full intensity.
        private static readonly (int limit, Rgb color)[] FrequencyBands =
        {
            (25, new Rgb(115, 2, 0)), // red (lowest)
            (60, new Rgb(115, 38, 0)), // red-orange
            (100, new Rgb(115, 73, 0)), // orange
            (150, new Rgb(115, 115, 5)), // yellow
            (190, new Rgb(0, 115, 5)), // green
            (210, new Rgb(5, 0, 115)), // blue
            (255, new Rgb(100, 0, 100)), // purple (highest)
        };

        /// <summary>Scheme name -> gradient; null for Frequency (a solid, frequency-driven color).</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, Func<int, Rgb>>> All = new[]
        {
            Pair("Rainbow", Rainbow),
            Pair("Green", pos => new Rgb(50 + pos / 2, 255 - pos / 2, 0)),
            Pair("Blue", pos => new Rgb(150 * pos / 255, 40, 255 - pos / 6)),
            Pair("Orange", pos => new Rgb(255 - pos / 4, 50 + 205 * pos / 255, 0)),
            Pair("Purple", pos => new Rgb(255 - pos, 0, 255)),
            Pair("Reverse Rainbow", ReverseRainbow),
            Pair(Frequency, null),
            Pair("Fade", _ => Wheel((int)(Clock.ElapsedMilliseconds / 128 % 256))), // ~33 s per lap
            Pair("White", _ => new Rgb(255, 255, 255)),
        };

        public static readonly IReadOnlyList<string> Names = All.Select(p => p.Key).ToList();

        private static KeyValuePair<string, Func<int, Rgb>> Pair(string name, Func<int, Rgb> gradient) =>
            new KeyValuePair<string, Func<int, Rgb>>(name, gradient);

        public static Func<int, Rgb> Gradient(string name) => All.FirstOrDefault(p => p.Key == name).Value;

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

        public static Rgb FrequencyTarget(double hz)
        {
            hz = Math.Max(FreqMin, Math.Min(FreqMax, hz));
            var normalized = (hz - FreqMin) * 255 / (FreqMax - FreqMin);
            var band = FrequencyBands.FirstOrDefault(b => normalized <= b.limit).color;
            return new Rgb(band.R * 255 / 115, band.G * 255 / 115, band.B * 255 / 115);
        }

        /// <summary>Static swatch for a scheme (time/frequency-driven ones show their range).</summary>
        public static Rgb Preview(string name, int pos)
        {
            if (name == Frequency)
                return FrequencyTarget(FreqMin + pos * (FreqMax - FreqMin) / 255.0);
            if (name == "Fade")
                return Wheel(pos);
            return Gradient(name)(pos);
        }
    }

    /// <summary>Eases toward the color for the current dominant frequency.</summary>
    internal sealed class FrequencyColor
    {
        private const double StepPerSecond = 5 * (255.0 / 115) * 86; // sketch: 5 per packet at ~86 packets/s
        private double _r, _g, _b;

        public Rgb Update(double hz, double dt)
        {
            var target = ColorSchemes.FrequencyTarget(hz);
            var step = StepPerSecond * dt;
            _r += Clamp(target.R - _r, step);
            _g += Clamp(target.G - _g, step);
            _b += Clamp(target.B - _b, step);
            return new Rgb((int)_r, (int)_g, (int)_b);
        }

        private static double Clamp(double delta, double step) => Math.Max(-step, Math.Min(step, delta));
    }
}
