using System;

namespace SoundRadar.Audio
{
    /// <summary>Power-weighted mean frequency of a block (drives the "Frequency" color).</summary>
    internal sealed class Spectrum
    {
        private const int MaxSize = 4096;
        private double[] _re = new double[512];
        private double[] _im = new double[512];

        /// <param name="interleaved">Interleaved stereo samples.</param>
        /// <param name="channel">0 = left, 1 = right.</param>
        public double AverageFrequency(float[] interleaved, int frames, int channel, int rate)
        {
            if (frames <= 1)
                return 0;
            var count = Math.Min(frames, MaxSize);
            var first = frames - count; // use the most recent samples
            var n = 2;
            while (n < count)
                n <<= 1;
            if (_re.Length != n)
            {
                _re = new double[n];
                _im = new double[n];
            }
            for (var i = 0; i < n; i++)
            {
                _re[i] = i < count ? interleaved[(first + i) * 2 + channel] : 0; // zero-pad to a power of two
                _im[i] = 0;
            }
            Fft(_re, _im);

            double weighted = 0, total = 0;
            for (var k = 0; k <= n / 2; k++)
            {
                var power = _re[k] * _re[k] + _im[k] * _im[k];
                weighted += power * k * rate / n;
                total += power;
            }
            return total > 0 ? weighted / total : 0;
        }

        private static void Fft(double[] re, double[] im)
        {
            var n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (var len = 2; len <= n; len <<= 1)
            {
                var angle = -2 * Math.PI / len;
                double wRe = Math.Cos(angle), wIm = Math.Sin(angle);
                for (var i = 0; i < n; i += len)
                {
                    double curRe = 1, curIm = 0;
                    for (var k = 0; k < len / 2; k++)
                    {
                        var a = i + k;
                        var b = a + len / 2;
                        var tRe = re[b] * curRe - im[b] * curIm;
                        var tIm = re[b] * curIm + im[b] * curRe;
                        re[b] = re[a] - tRe;
                        im[b] = im[a] - tIm;
                        re[a] += tRe;
                        im[a] += tIm;
                        var nextRe = curRe * wRe - curIm * wIm;
                        curIm = curRe * wIm + curIm * wRe;
                        curRe = nextRe;
                    }
                }
            }
        }
    }
}
