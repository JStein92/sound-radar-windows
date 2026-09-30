using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SoundRadar.Audio
{
    // Minimal WASAPI / Core Audio interop.
    //
    // Endpoint loopback taps the default output device *after* Windows applies the
    // accessibility "Mono audio" downmix, so left == right while Mono is on.
    // Process loopback (Windows 10 2004+) taps app streams *before* they are mixed into
    // the endpoint, which keeps the real stereo image while the user hears mono.

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;

        public static WaveFormatEx StereoFloat(int rate) => new WaveFormatEx
        {
            FormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            Channels = 2,
            SamplesPerSec = (uint)rate,
            BitsPerSample = 32,
            BlockAlign = 8,
            AvgBytesPerSec = (uint)rate * 8,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientActivationParams
    {
        public int ActivationType; // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
        public uint TargetProcessId;
        public int ProcessLoopbackMode; // 0 = include tree, 1 = exclude tree
    }

    // PROPVARIANT holding a VT_BLOB (layout matches on both x86 and x64).
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariantBlob
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService([In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        void Activate([In] ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(uint access, [MarshalAs(UnmanagedType.IUnknown)] out object properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out uint state);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, uint stateMask, [MarshalAs(UnmanagedType.IUnknown)] out object devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCoClass { }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // IAudioSessionControl slots, kept so the vtable lines up.
        void GetState(out int state);
        void GetDisplayName(IntPtr name);
        void SetDisplayName(IntPtr name, IntPtr context);
        void GetIconPath(IntPtr path);
        void SetIconPath(IntPtr path, IntPtr context);
        void GetGroupingParam(IntPtr param);
        void SetGroupingParam(IntPtr param, IntPtr context);
        void RegisterAudioSessionNotification(IntPtr client);
        void UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        void GetSessionIdentifier(IntPtr id);
        void GetSessionInstanceIdentifier(IntPtr id);
        void GetProcessId(out uint pid);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        // IAudioSessionManager
        void GetAudioSessionControl(IntPtr sessionGuid, uint flags, IntPtr control);
        void GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, IntPtr volume);
        // IAudioSessionManager2
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    // Marker: the completion handler must be agile (callable from any apartment).
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    internal sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }

    internal readonly struct AudioSession
    {
        public AudioSession(uint pid, string exeName, bool active)
        {
            Pid = pid;
            ExeName = exeName;
            Active = active;
        }

        public uint Pid { get; }
        public string ExeName { get; }
        public bool Active { get; }
    }

    internal static class Wasapi
    {
        public const uint ClsCtxAll = 23;
        public const int ERender = 0;
        public const int EConsole = 0;

        public static readonly Guid IidAudioClient = typeof(IAudioClient).GUID;
        public static readonly Guid IidAudioCaptureClient = typeof(IAudioCaptureClient).GUID;
        public static readonly Guid IidAudioSessionManager2 = typeof(IAudioSessionManager2).GUID;

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        private static extern void ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [In] ref PropVariantBlob activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint StillActive = 259;

        private static int? _windowsBuild;

        public static int WindowsBuild
        {
            get
            {
                if (_windowsBuild == null)
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        _windowsBuild = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var build)
                            ? build
                            : Environment.OSVersion.Version.Build;
                    }
                }
                return _windowsBuild.Value;
            }
        }

        /// <summary>Process loopback shipped in Windows 10 2004 (build 19041).</summary>
        public static bool ProcessLoopbackSupported => WindowsBuild >= 19041;

        /// <summary>Whether the Windows accessibility "Mono audio" switch is on.</summary>
        public static bool WindowsMonoEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Multimedia\Audio"))
            {
                return key?.GetValue("AccessibilityMonoMixState") is int value && value == 1;
            }
        }

        public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();

        public static IMMDevice DefaultRenderDevice(IMMDeviceEnumerator enumerator)
        {
            enumerator.GetDefaultAudioEndpoint(ERender, EConsole, out var device);
            return device;
        }

        public static string DefaultRenderDeviceId(IMMDeviceEnumerator enumerator)
        {
            var device = DefaultRenderDevice(enumerator);
            try
            {
                device.GetId(out var id);
                return id;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }

        public static IAudioClient ActivateEndpointLoopback(IMMDeviceEnumerator enumerator)
        {
            var device = DefaultRenderDevice(enumerator);
            try
            {
                var iid = IidAudioClient;
                device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var client);
                return (IAudioClient)client;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }

        public static IAudioClient ActivateProcessLoopback(uint targetPid, bool includeTree)
        {
            var parameters = new AudioClientActivationParams
            {
                ActivationType = 1,
                TargetProcessId = targetPid,
                ProcessLoopbackMode = includeTree ? 0 : 1,
            };
            var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
            try
            {
                Marshal.StructureToPtr(parameters, paramsPtr, false);
                var prop = new PropVariantBlob
                {
                    vt = 65, // VT_BLOB
                    cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                    pBlobData = paramsPtr,
                };
                var handler = new ActivationHandler();
                ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", IidAudioClient, ref prop, handler, out var operation);
                try
                {
                    if (!handler.Done.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Timed out activating process loopback capture.");
                    operation.GetActivateResult(out var hr, out var client);
                    Marshal.ThrowExceptionForHR(hr);
                    return (IAudioClient)client;
                }
                finally
                {
                    Marshal.ReleaseComObject(operation);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(paramsPtr);
            }
        }

        /// <summary>
        /// Processes that own an audio session on the default output, excluding
        /// system sounds and SoundRadar itself.
        /// </summary>
        public static List<AudioSession> ListAudioSessions()
        {
            var result = new List<AudioSession>();
            var ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var enumerator = CreateEnumerator();
            IMMDevice device = null;
            try
            {
                device = DefaultRenderDevice(enumerator);
                var iid = IidAudioSessionManager2;
                device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var managerObj);
                var manager = (IAudioSessionManager2)managerObj;
                manager.GetSessionEnumerator(out var sessions);
                sessions.GetCount(out var count);
                for (var i = 0; i < count; i++)
                {
                    sessions.GetSession(i, out var sessionObj);
                    var control = (IAudioSessionControl2)sessionObj;
                    control.GetProcessId(out var pid);
                    control.GetState(out var state);
                    Marshal.ReleaseComObject(control);
                    if (pid == 0 || pid == ownPid)
                        continue;
                    var name = ProcessImageName(pid);
                    if (name != null)
                        result.Add(new AudioSession(pid, name, state == 1));
                }
                Marshal.ReleaseComObject(sessions);
                Marshal.ReleaseComObject(manager);
            }
            finally
            {
                if (device != null)
                    Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
            return result;
        }

        /// <summary>Executable file name (e.g. <c>game.exe</c>) for a PID, or null.</summary>
        public static string ProcessImageName(uint pid)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var size = 1024u;
                var buffer = new StringBuilder((int)size);
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? Path.GetFileName(buffer.ToString()) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public static bool ProcessAlive(uint pid)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
                return false;
            try
            {
                return GetExitCodeProcess(handle, out var code) && code == StillActive;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}
