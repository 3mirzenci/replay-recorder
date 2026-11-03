using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ReplayRecorder.Recorder.Audio;

/// <summary>
/// "ReplayRecorder.Recorder.exe --diag-audio": lists every audio endpoint and measures what WASAPI
/// loopback actually delivers from each output for a few seconds (play something meanwhile).
/// Writes a report next to the logs and returns its path.
/// </summary>
static class AudioDiagnostics
{
    public static string Run(int seconds = 4)
    {
        var sb = new StringBuilder();
        using var en = new MMDeviceEnumerator();
        string Def(DataFlow f, Role r) => en.HasDefaultAudioEndpoint(f, r) ? en.GetDefaultAudioEndpoint(f, r).FriendlyName : "(none)";
        sb.AppendLine($"Default output (multimedia):     {Def(DataFlow.Render, Role.Multimedia)}");
        sb.AppendLine($"Default output (communications): {Def(DataFlow.Render, Role.Communications)}");
        sb.AppendLine($"Default mic (communications):    {Def(DataFlow.Capture, Role.Communications)}");
        sb.AppendLine($"Default mic (multimedia):        {Def(DataFlow.Capture, Role.Multimedia)}");
        sb.AppendLine();

        var captures = new List<(MMDevice Dev, WasapiCapture Cap, float[] Peak, long[] Bytes)>();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var cap = new WasapiLoopbackCapture(d);
                var peak = new float[1]; var bytes = new long[1];
                var fmt = cap.WaveFormat;
                cap.DataAvailable += (_, e) =>
                {
                    bytes[0] += e.BytesRecorded;
                    if (fmt.BitsPerSample != 32) return;
                    for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
                        peak[0] = Math.Max(peak[0], Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
                };
                cap.StartRecording();
                captures.Add((d, cap, peak, bytes));
            }
            catch (Exception ex) { sb.AppendLine($"[render] {d.FriendlyName}: cannot open loopback ({ex.Message})"); }
        }

        Thread.Sleep(seconds * 1000);

        foreach (var (dev, cap, peak, bytes) in captures)
        {
            cap.StopRecording();
            double db = peak[0] > 0 ? 20 * Math.Log10(peak[0]) : double.NegativeInfinity;
            sb.AppendLine($"[render] {dev.FriendlyName}\n         id={dev.ID}\n         format={cap.WaveFormat}  received={bytes[0] / 1024} KB  peak={(double.IsInfinity(db) ? "silent" : $"{db:0.0} dBFS")}");
            cap.Dispose();
        }
        sb.AppendLine();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            sb.AppendLine($"[mic]    {d.FriendlyName}\n         id={d.ID}");

        var path = Path.Combine(ReplayRecorder.Core.Paths.LogFolder, "audio-diagnostics.txt");
        Directory.CreateDirectory(ReplayRecorder.Core.Paths.LogFolder);
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
