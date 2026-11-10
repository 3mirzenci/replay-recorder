using System.Runtime.InteropServices;
using ReplayRecorder.Core;

namespace ReplayRecorder.Recorder.Audio;

/// <summary>
/// Windows process loopback (Windows 10 2004+): records only the audio of one process tree
/// (<c>include</c>), or everything except it. A documented Windows API, no hooking. It isn't tied to
/// an output device, so the device setting doesn't apply. Pushes 48 kHz float stereo into a sink.
/// </summary>
sealed class ProcessLoopback : IDisposable
{
    public static bool Supported => Environment.OSVersion.Version.Build >= 19041;

    readonly ClockedAudioSink _sink;
    readonly IAudioClient _client;
    readonly IAudioCaptureClient _capture;
    readonly AutoResetEvent _ready = new(false);
    readonly Thread _thread;
    volatile bool _stop;

    /// <summary>The capture loop hit an error and stopped (e.g. the audio service restarted); open a new one.</summary>
    public bool Dead { get; private set; }

    /// <summary>Starts capturing; throws when Windows refuses (too old, process gone...).</summary>
    public ProcessLoopback(int pid, bool include, ClockedAudioSink sink)
    {
        _sink = sink;
        _client = Activate(pid, include);
        // GetMixFormat isn't supported here: ask for 48 kHz float stereo, Windows converts.
        var fmt = new WaveFormatEx { Tag = 3, Channels = 2, SampleRate = 48000, BitsPerSample = 32, BlockAlign = 8, AvgBytesPerSec = 48000 * 8 };
        const uint Loopback = 0x00020000, EventCallback = 0x00040000, AutoConvert = 0x80000000, SrcDefaultQuality = 0x08000000;
        _client.Initialize(0, Loopback | EventCallback | AutoConvert | SrcDefaultQuality, 200_000, 0, ref fmt, IntPtr.Zero);
        _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
        var iid = typeof(IAudioCaptureClient).GUID;
        _capture = (IAudioCaptureClient)_client.GetService(ref iid);
        _client.Start();
        _thread = new Thread(Run) { IsBackground = true, Name = $"loopback-{pid}", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Run()
    {
        var buf = new float[48000 * 2];
        try
        {
            while (!_stop)
            {
                _ready.WaitOne(100);
                while (!_stop)
                {
                    _capture.GetNextPacketSize(out var next);
                    if (next == 0) break;
                    _capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                    int n = (int)Math.Min(frames * 2, (uint)buf.Length);
                    if ((flags & 2) != 0) Array.Clear(buf, 0, n); // AUDCLNT_BUFFERFLAGS_SILENT
                    else Marshal.Copy(data, buf, 0, n);
                    _capture.ReleaseBuffer(frames);
                    _sink.Push(buf.AsSpan(0, n));
                }
            }
        }
        catch (Exception ex) when (!_stop) { Log.Warn($"Process loopback stopped: {ex.Message}"); Dead = true; }
        catch { }
    }

    public void Dispose()
    {
        _stop = true;
        _ready.Set();
        _thread.Join(500);
        try { _client.Stop(); } catch { }
        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
        _ready.Dispose();
    }

    // ------------------------------------------------------------------ activation

    static IAudioClient Activate(int pid, bool include)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK, { TargetProcessId, ProcessLoopbackMode } }
        var p = Marshal.AllocHGlobal(12);
        var pv = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(p, 0, 1);
            Marshal.WriteInt32(p, 4, pid);
            Marshal.WriteInt32(p, 8, include ? 0 : 1);
            // PROPVARIANT holding a VT_BLOB that points at the params.
            for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65);
            Marshal.WriteInt32(pv, 8, 12);
            Marshal.WriteIntPtr(pv, 16, p);

            var done = new Completion();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", ref iid, pv, done, out var op);
            if (!done.Signal.Wait(5000)) throw new TimeoutException("activation timed out");
            op.GetActivateResult(out var hr, out var unk);
            Marshal.ThrowExceptionForHR(hr);
            return (IAudioClient)unk;
        }
        finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(pv); }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public ushort Tag, Channels;
        public uint SampleRate, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    // The completion may arrive on any thread: IAgileObject says so.
    sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Signal = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Signal.Set();
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint padding);
        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long def, out long min);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetService(ref Guid iid);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }
}
