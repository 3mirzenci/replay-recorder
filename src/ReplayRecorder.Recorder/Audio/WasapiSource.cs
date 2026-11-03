using ReplayRecorder.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace ReplayRecorder.Recorder.Audio;

/// <summary>
/// Captures one real WASAPI endpoint (no virtual devices) and pushes 48 kHz float audio
/// into a <see cref="ClockedAudioSink"/>. Loopback mode records everything you hear on the
/// default output device, Discord voice included. The source re-opens itself when the
/// Windows default device changes or the device disappears, so a mid-session headset
/// switch doesn't leave you with silent clips.
/// </summary>
sealed class WasapiSource : IDisposable, IMMNotificationClient
{
    readonly bool _loopback;
    readonly string _fixedDeviceId; // empty = follow the Windows default
    readonly ClockedAudioSink _sink;
    readonly MMDeviceEnumerator _enumerator = new();
    readonly object _gate = new();
    WasapiCapture? _capture;
    MMDevice? _device;
    Converter? _conv;
    System.Threading.Timer? _retry;
    volatile bool _disposed;

    public string DeviceName { get; private set; } = "(none)";

    public WasapiSource(bool loopback, string deviceId, ClockedAudioSink sink)
    {
        _loopback = loopback;
        _fixedDeviceId = deviceId;
        _sink = sink;
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public void Start() => Open();

    void Open()
    {
        lock (_gate)
        {
            if (_disposed) return;
            CloseCapture();
            try
            {
                _device = ResolveDevice();
                if (_device == null) { Log.Warn($"{Kind}: no device available, retrying"); ScheduleRetry(); return; }

                // Shared mode, event-driven, 20 ms buffer: low latency without busy CPU.
                _capture = _loopback ? new WasapiLoopbackCapture(_device) : new WasapiCapture(_device, true, 20);
                _conv = new Converter(_capture.WaveFormat, _sink.Channels);
                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += OnStopped;
                _capture.StartRecording();
                DeviceName = _device.FriendlyName;
                Log.Info($"{Kind}: capturing \"{DeviceName}\" ({_capture.WaveFormat})");
            }
            catch (Exception ex)
            {
                Log.Warn($"{Kind}: open failed ({ex.Message}), retrying");
                CloseCapture();
                ScheduleRetry();
            }
        }
    }

    string Kind => _loopback ? "System audio" : "Microphone";

    MMDevice? ResolveDevice()
    {
        if (!string.IsNullOrEmpty(_fixedDeviceId))
        {
            try
            {
                var d = _enumerator.GetDevice(_fixedDeviceId);
                if (d.State == DeviceState.Active) return d;
                Log.Warn($"{Kind}: selected device is not active, falling back to default");
            }
            catch { Log.Warn($"{Kind}: selected device not found, falling back to default"); }
        }
        var flow = _loopback ? DataFlow.Render : DataFlow.Capture;
        var role = _loopback ? Role.Multimedia : Role.Communications;
        if (!_enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
        var def = _enumerator.GetDefaultAudioEndpoint(flow, role);
        if (_loopback && IsVirtual(def))
        {
            var physical = PhysicalOutputFor(def);
            if (physical != null)
            {
                Log.Info($"{Kind}: default output \"{def.FriendlyName}\" is a virtual device (its loopback is silent); " +
                         $"recording the real device \"{physical.FriendlyName}\" instead");
                def.Dispose();
                return physical;
            }
            Log.Warn($"{Kind}: default output \"{def.FriendlyName}\" looks virtual and no physical output was found; clips may be silent");
        }
        return def;
    }

    /// <summary>
    /// Virtual mixers (NGENUITY, Sonar, VB-Cable, Voicemeeter...) present a fake output and forward
    /// the mix to the real device in their own process; WASAPI loopback of the fake one is silent.
    /// </summary>
    public static bool IsVirtual(MMDevice d)
    {
        var n = d.FriendlyName;
        return n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Sonar", StringComparison.OrdinalIgnoreCase)
            || n.Contains("NGENUITY", StringComparison.OrdinalIgnoreCase)
            || n.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Picks the physical output a virtual device most likely routes to.</summary>
    MMDevice? PhysicalOutputFor(MMDevice virtualDev)
    {
        var candidates = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(d => !IsVirtual(d)).ToList();
        if (candidates.Count == 0) return null;

        // 1) Same brand as the virtual device, e.g. "HyperX Virtual Audio Device" -> "HyperX Cloud III".
        var brand = virtualDev.FriendlyName.Split('(', ')', ' ', '-')
            .FirstOrDefault(w => w.Length > 3 && candidates.Any(c => c.FriendlyName.Contains(w, StringComparison.OrdinalIgnoreCase))
                                 && !w.Equals("Audio", StringComparison.OrdinalIgnoreCase));
        var pick = brand == null ? null : candidates.First(c => c.FriendlyName.Contains(brand, StringComparison.OrdinalIgnoreCase));

        // 2) Otherwise a USB/Bluetooth headset, 3) otherwise anything that isn't a monitor's HDMI/DP audio.
        pick ??= candidates.FirstOrDefault(c => c.FriendlyName.Contains("Headset", StringComparison.OrdinalIgnoreCase)
                                                || c.FriendlyName.Contains("USB", StringComparison.OrdinalIgnoreCase));
        pick ??= candidates.FirstOrDefault(c => !c.FriendlyName.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase)
                                                && !c.FriendlyName.Contains("Display Audio", StringComparison.OrdinalIgnoreCase));
        pick ??= candidates[0];
        foreach (var c in candidates) if (c != pick) c.Dispose();
        return pick;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var conv = _conv;
        if (conv == null || e.BytesRecorded == 0) return;
        _sink.Push(conv.Convert(e.Buffer, e.BytesRecorded));
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        if (e.Exception != null)
        {
            Log.Warn($"{Kind}: device stopped ({e.Exception.Message}), re-opening");
            ScheduleRetry();
        }
    }

    void ScheduleRetry()
    {
        _retry?.Dispose();
        _retry = new System.Threading.Timer(_ => Open(), null, 1000, Timeout.Infinite);
    }

    void CloseCapture()
    {
        if (_capture == null) return;
        _capture.DataAvailable -= OnData;
        _capture.RecordingStopped -= OnStopped;
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        _capture = null;
        _device?.Dispose();
        _device = null;
    }

    // --- IMMNotificationClient: follow default-device changes ---
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (!string.IsNullOrEmpty(_fixedDeviceId)) return;
        bool mine = _loopback ? flow == DataFlow.Render && role == Role.Multimedia
                              : flow == DataFlow.Capture && role == Role.Communications;
        if (!mine) return;
        Log.Info($"{Kind}: Windows default device changed, switching");
        // Never re-open inside the COM callback; hop to the thread pool.
        ThreadPool.QueueUserWorkItem(_ => Open());
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (newState == DeviceState.Active && _capture == null) ThreadPool.QueueUserWorkItem(_ => Open());
    }

    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _disposed = true;
        _retry?.Dispose();
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        lock (_gate) CloseCapture();
        _enumerator.Dispose();
    }

    /// <summary>Converts any WASAPI mix format to interleaved 48 kHz float with N channels.</summary>
    sealed class Converter
    {
        static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
        readonly WaveFormat _fmt;
        readonly int _outCh;
        readonly WdlResampler? _resampler;
        float[] _frames = Array.Empty<float>();
        float[] _out = Array.Empty<float>();

        public Converter(WaveFormat fmt, int outChannels)
        {
            _fmt = fmt;
            _outCh = outChannels;
            if (fmt.SampleRate != ClockedAudioSink.Rate)
            {
                _resampler = new WdlResampler();
                _resampler.SetMode(true, 2, false);
                _resampler.SetFilterParms();
                _resampler.SetFeedMode(true);
                _resampler.SetRates(fmt.SampleRate, ClockedAudioSink.Rate);
            }
        }

        public ReadOnlySpan<float> Convert(byte[] buf, int bytes)
        {
            int inCh = _fmt.Channels;
            int bps = _fmt.BitsPerSample / 8;
            int frames = bytes / (bps * inCh);
            if (_frames.Length < frames * _outCh) _frames = new float[frames * _outCh];

            bool isFloat = _fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                (_fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);

            for (int f = 0; f < frames; f++)
            {
                int o = f * bps * inCh;
                float Read(int c)
                {
                    int p = o + c * bps;
                    if (isFloat && bps == 4) return BitConverter.ToSingle(buf, p);
                    return bps switch
                    {
                        2 => BitConverter.ToInt16(buf, p) / 32768f,
                        3 => ((buf[p] << 8 | buf[p + 1] << 16 | buf[p + 2] << 24) >> 8) / 8388608f,
                        4 => BitConverter.ToInt32(buf, p) / 2147483648f,
                        _ => 0f,
                    };
                }

                if (_outCh == 1)
                {
                    float s = 0;
                    for (int c = 0; c < inCh; c++) s += Read(c);
                    _frames[f] = s / inCh;
                }
                else if (inCh == 1)
                {
                    _frames[f * 2] = _frames[f * 2 + 1] = Read(0);
                }
                else if (inCh == 2)
                {
                    _frames[f * 2] = Read(0);
                    _frames[f * 2 + 1] = Read(1);
                }
                else
                {
                    // Surround -> stereo (FL FR FC LFE BL BR SL SR), standard -3 dB center/surround fold-down.
                    float c3 = Read(2) * 0.707f;
                    float l = Read(0) + c3, r = Read(1) + c3;
                    if (inCh >= 6) { l += Read(4) * 0.707f; r += Read(5) * 0.707f; }
                    if (inCh >= 8) { l += Read(6) * 0.707f; r += Read(7) * 0.707f; }
                    _frames[f * 2] = l * 0.5f;
                    _frames[f * 2 + 1] = r * 0.5f;
                }
            }

            if (_resampler == null) return _frames.AsSpan(0, frames * _outCh);

            int want = _resampler.ResamplePrepare(frames, _outCh, out var inBuf, out var inOff);
            Array.Copy(_frames, 0, inBuf, inOff, Math.Min(want, frames) * _outCh);
            int maxOut = frames * ClockedAudioSink.Rate / _fmt.SampleRate + 64;
            if (_out.Length < maxOut * _outCh) _out = new float[maxOut * _outCh];
            int got = _resampler.ResampleOut(_out, 0, frames, maxOut, _outCh);
            return _out.AsSpan(0, got * _outCh);
        }
    }
}
