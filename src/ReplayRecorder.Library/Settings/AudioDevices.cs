using ReplayRecorder.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ReplayRecorder.Library.SettingsUi;

sealed record DeviceChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

static class AudioDevices
{
    public static List<DeviceChoice> Outputs()
    {
        using var en = new MMDeviceEnumerator();
        using var auto = OutputDevices.Automatic(en);
        // Shows the device "Automatic" lands on, so a wrong guess (e.g. two USB headsets) is visible and can be overridden.
        var list = new List<DeviceChoice> { new("", auto == null ? "Automatic (real device behind the Windows default)".T() : "Automatic (now: {0})".T(auto.FriendlyName)) };
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            list.Add(new(d.ID, OutputDevices.IsVirtual(d.FriendlyName) ? "{0}  (virtual: may record silence)".T(d.FriendlyName) : d.FriendlyName));
        return list;
    }

    public static List<DeviceChoice> Mics()
    {
        var list = new List<DeviceChoice> { new("", "Windows default (communications) mic".T()) };
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            list.Add(new(d.ID, d.FriendlyName));
        return list;
    }
}

/// <summary>Live peak meter for one device (mic, or loopback of an output) while settings are open.</summary>
sealed class LevelMeter : IDisposable
{
    WasapiCapture? _cap;
    volatile float _peak;

    /// <summary>Peak in dBFS since the last read (decays), -60 when silent.</summary>
    public double ReadDb()
    {
        float p = _peak;
        _peak = p * 0.6f;
        return p > 0.001 ? Math.Max(-60, 20 * Math.Log10(p)) : -60;
    }

    public void Start(string deviceId, bool loopback)
    {
        Stop();
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice dev;
            if (!string.IsNullOrEmpty(deviceId)) dev = en.GetDevice(deviceId);
            else if (loopback) dev = OutputDevices.Automatic(en) ?? throw new InvalidOperationException("no output");
            else dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            _cap = loopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev, true, 30);
            var fmt = _cap.WaveFormat;
            _cap.DataAvailable += (_, e) =>
            {
                float peak = 0;
                if (fmt.BitsPerSample == 32)
                    for (int i = 0; i + 4 <= e.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
                else if (fmt.BitsPerSample == 16)
                    for (int i = 0; i + 2 <= e.BytesRecorded; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f));
                if (peak > _peak) _peak = peak;
            };
            _cap.StartRecording();
        }
        catch { Stop(); }
    }

    public void Stop()
    {
        try { _cap?.StopRecording(); } catch { }
        _cap?.Dispose();
        _cap = null;
        _peak = 0;
    }

    public void Dispose() => Stop();
}
