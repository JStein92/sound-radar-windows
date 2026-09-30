using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoundRadar.Audio
{
    internal enum CaptureMode
    {
        /// <summary>The default output device's mix (flattened by Windows Mono).</summary>
        Endpoint,
        /// <summary>Every process except a tree (SoundRadar itself), before the Mono downmix.</summary>
        ExcludeProcess,
        /// <summary>Only one process tree (e.g. the game), before the Mono downmix.</summary>
        IncludeProcess,
    }

    /// <summary>
    /// Pulls interleaved stereo float32 frames from a WASAPI loopback client.
    /// Create, use and dispose it on one MTA thread.
    /// </summary>
    internal sealed class LoopbackCapture : IDisposable
    {
        private const uint StreamFlagsLoopback = 0x00020000;
        private const uint StreamFlagsEventCallback = 0x00040000;
        private const uint StreamFlagsSrcDefaultQuality = 0x08000000;
        private const uint StreamFlagsAutoConvertPcm = 0x80000000;
        private const uint BufferFlagsSilent = 0x2;

        private IAudioClient _client;
        private IAudioCaptureClient _capture;
        private readonly AutoResetEvent _event = new AutoResetEvent(false);

        public LoopbackCapture(CaptureMode mode, IMMDeviceEnumerator enumerator, uint targetPid = 0, int rate = 48000)
        {
            Mode = mode;
            // AUTOCONVERTPCM lets us always ask for stereo float32; the engine inserts a
            // channel matrixer / resampler when the source differs.
            var flags = StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality;
            long bufferDuration;

            if (mode == CaptureMode.Endpoint)
            {
                _client = Wasapi.ActivateEndpointLoopback(enumerator);
                _client.GetMixFormat(out var mixPtr);
                try
                {
                    rate = (int)Marshal.PtrToStructure<WaveFormatEx>(mixPtr).SamplesPerSec;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(mixPtr);
                }
                bufferDuration = 0;
            }
            else
            {
                // Process loopback has no mix format (GetMixFormat is E_NOTIMPL), so we pick
                // one and must pass an explicit buffer duration.
                _client = Wasapi.ActivateProcessLoopback(targetPid, mode == CaptureMode.IncludeProcess);
                bufferDuration = 200_000; // 20 ms in 100 ns units
            }

            Rate = rate;
            var format = WaveFormatEx.StereoFloat(rate);
            var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            try
            {
                Marshal.StructureToPtr(format, formatPtr, false);
                _client.Initialize(0 /* shared */, flags, bufferDuration, 0, formatPtr, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
            }

            _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
            var iid = Wasapi.IidAudioCaptureClient;
            _client.GetService(ref iid, out var capture);
            _capture = (IAudioCaptureClient)capture;
            _client.Start();
        }

        public CaptureMode Mode { get; }
        public int Rate { get; }

        /// <summary>
        /// Appends everything captured since the last call to <paramref name="buffer"/>
        /// (interleaved L/R floats, grown as needed) and returns the number of frames.
        /// Returns 0 when nothing arrived before the timeout.
        /// </summary>
        public int Read(ref float[] buffer, int timeoutMs)
        {
            _event.WaitOne(timeoutMs);
            var total = 0;
            while (true)
            {
                _capture.GetNextPacketSize(out var available);
                if (available == 0)
                    break;
                _capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                var needed = (total + (int)frames) * 2;
                if (buffer.Length < needed)
                    Array.Resize(ref buffer, Math.Max(needed, buffer.Length * 2));
                if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                    Array.Clear(buffer, total * 2, (int)frames * 2);
                else
                    Marshal.Copy(data, buffer, total * 2, (int)frames * 2);
                _capture.ReleaseBuffer(frames);
                total += (int)frames;
            }
            return total;
        }

        public void Dispose()
        {
            try
            {
                _client?.Stop();
            }
            catch (COMException)
            {
                // Device already gone.
            }
            if (_capture != null)
                Marshal.FinalReleaseComObject(_capture);
            if (_client != null)
                Marshal.FinalReleaseComObject(_client);
            _capture = null;
            _client = null;
            _event.Dispose();
        }
    }
}
