using System.Media;

namespace ReplayRecorder.Recorder;

/// <summary>
/// Short chimes for save / failure, generated once in memory. Kept quiet and brief because the
/// recorder hears its own output: the chime ends up faintly in the buffer after the save.
/// </summary>
static class SaveSound
{
    static readonly Lazy<SoundPlayer> Ok = new(() => Make((880, 0.07), (1320, 0.11)));
    static readonly Lazy<SoundPlayer> Error = new(() => Make((440, 0.12), (330, 0.18)));

    public static void Saved() => Play(Ok);
    public static void Failed() => Play(Error);

    static void Play(Lazy<SoundPlayer> p)
    {
        try { p.Value.Play(); } // async, doesn't block the caller
        catch (Exception ex) { Core.Log.Warn($"Save sound failed: {ex.Message}"); }
    }

    /// <summary>Back-to-back sine tones with short fades, as a 16-bit mono WAV.</summary>
    static SoundPlayer Make(params (double Hz, double Seconds)[] tones)
    {
        const int rate = 44100;
        const double volume = 0.22;
        var samples = new List<short>();
        foreach (var (hz, sec) in tones)
        {
            int n = (int)(rate * sec), fade = rate / 200; // 5 ms fades: no clicks
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - 1 - i) / (double)fade) * Math.Exp(-2.5 * i / n);
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * env * volume * short.MaxValue));
            }
        }

        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int data = samples.Count * 2;
            w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8); w.Write(data);
            foreach (var s in samples) w.Write(s);
        }
        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        return player;
    }
}
