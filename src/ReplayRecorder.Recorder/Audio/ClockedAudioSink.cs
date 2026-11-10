using System.IO.Pipes;
using ReplayRecorder.Core;

namespace ReplayRecorder.Recorder.Audio;

/// <summary>
/// Feeds one continuous 48 kHz float PCM stream into ffmpeg through a named pipe.
///
/// ffmpeg timestamps a raw PCM input purely by sample count, so this class is what keeps
/// audio aligned with video: sample N is always written for wall-clock time
/// <c>epoch + N / 48000</c> on the QPC clock (Stopwatch), the same clock Windows stamps captured
/// video frames with, so wall-clock jumps (time sync, manual changes) can't pull them apart.
/// Captured audio goes into a small jitter buffer; a pacing thread drains it at exactly
/// real-time speed, nudging the drain rate by up to ±0.5 % to absorb clock drift between
/// the sound card and QPC, and writing silence whenever the device delivers
/// nothing (WASAPI loopback goes quiet when nothing is playing, or while a device is being
/// re-opened after a change).
/// </summary>
sealed class ClockedAudioSink : IDisposable
{
    public const int Rate = PcmConverter.Rate;

    // Audio is written this far behind real time. KeepMs of it sits in the buffer as
    // jitter headroom; the rest covers WASAPI's own capture latency (~10-20 ms).
    const int KeepMs = 40;
    const int LagMs = 55;
    const int DropAboveMs = 400;

    public int Channels { get; }
    public string PipeName { get; }
    public string PipePath => $@"\\.\pipe\{PipeName}";

    /// <summary>Linear gain applied to everything pushed in.</summary>
    public float Gain { get; set; } = 1f;

    /// <summary>When true the sink writes silence (mic muted), keeping the track present.</summary>
    public bool Muted { get; set; }

    /// <summary>Peak level of recently pushed audio, 0..1, for meters.</summary>
    public float Peak => _peak;

    readonly NamedPipeServerStream _pipe;
    readonly float[] _ring;
    int _ringStart, _ringCount; // in samples (frames * channels)
    readonly object _lock = new();
    volatile float _peak;
    double _smoothedLevel = KeepMs;
    double _readPhase; // fractional input position carried between ticks
    bool _running; // false = idle (outputting silence until the buffer refills)

    Thread? _thread;
    volatile bool _stop;
    long _epochQpc;     // Stopwatch.GetTimestamp() at timeline zero
    long _framesWritten;

    public ClockedAudioSink(string pipeName, int channels)
    {
        PipeName = pipeName;
        Channels = channels;
        _ring = new float[Rate * channels * 2]; // 2 s of headroom
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 1 << 20); // only our own user's ffmpeg can connect
    }

    /// <summary>Starts pacing against <paramref name="epochQpc"/> (Stopwatch timestamp of timeline zero).</summary>
    public void Start(long epochQpc)
    {
        _epochQpc = epochQpc;
        _thread = new Thread(Run) { IsBackground = true, Name = $"audio-{PipeName}", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Adds interleaved 48 kHz samples (Channels per frame).</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        float g = Gain, peak = 0;
        lock (_lock)
        {
            foreach (var s0 in samples)
            {
                var s = s0 * g;
                var a = Math.Abs(s);
                if (a > peak) peak = a;
                if (_ringCount == _ring.Length) // overflow: drop oldest
                {
                    _ringStart = (_ringStart + 1) % _ring.Length;
                    _ringCount--;
                }
                _ring[(_ringStart + _ringCount) % _ring.Length] = s;
                _ringCount++;
            }
        }
        _peak = Math.Max(peak, _peak * 0.8f);
    }

    void Run()
    {
        try
        {
            _pipe.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(15));
            if (!_pipe.IsConnected) { Log.Warn($"{PipeName}: ffmpeg never connected"); return; }

            var outBuf = new float[Rate * Channels]; // up to 1 s per tick
            var bytes = new byte[outBuf.Length * 4];
            while (!_stop)
            {
                long lagFrames = (long)Rate * LagMs / 1000;
                long nowFrames = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - _epochQpc) * (double)Rate / System.Diagnostics.Stopwatch.Frequency);
                long need = nowFrames - lagFrames - _framesWritten;
                if (need <= 0) { Thread.Sleep(5); continue; }

                // After a long stall (e.g. machine resumed from sleep) write the gap in 1 s chunks.
                int n = (int)Math.Min(need, Rate);
                Fill(outBuf.AsSpan(0, n * Channels), n);
                Buffer.BlockCopy(outBuf, 0, bytes, 0, n * Channels * 4);
                _pipe.Write(bytes, 0, n * Channels * 4);
                _framesWritten += n;
                if (need <= Rate) Thread.Sleep(10);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AggregateException)
        {
            // ffmpeg exited; the engine notices and restarts everything.
        }
        catch (Exception ex)
        {
            Log.Error($"{PipeName} pacing thread crashed", ex);
        }
    }

    /// <summary>Produces exactly <paramref name="frames"/> output frames from the jitter buffer.</summary>
    void Fill(Span<float> dst, int frames)
    {
        int ch = Channels;
        lock (_lock)
        {
            int availFrames = _ringCount / ch;
            double levelMs = availFrames * 1000.0 / Rate;
            _smoothedLevel += (levelMs - _smoothedLevel) * 0.05;

            if (levelMs > DropAboveMs) // way behind (stall or startup): jump to the newest audio
            {
                int drop = availFrames - Rate * KeepMs / 1000;
                Discard(drop * ch);
                availFrames -= drop;
                _smoothedLevel = KeepMs;
            }

            if (!_running && levelMs >= KeepMs) _running = true;

            if (!_running || Muted)
            {
                dst.Clear();
                if (Muted) Discard(Math.Max(0, availFrames - Rate * KeepMs / 1000) * ch);
                return;
            }

            // Drift control: read slightly faster when the buffer is fuller than target.
            double ratio = 1.0 + Math.Clamp((_smoothedLevel - KeepMs) / 2000.0, -0.005, 0.005);
            double pos = _readPhase;
            int written = 0;
            while (written < frames)
            {
                int i0 = (int)pos;
                if (i0 + 1 >= availFrames) break; // ran dry
                float t = (float)(pos - i0);
                for (int c = 0; c < ch; c++)
                {
                    float a = _ring[(_ringStart + i0 * ch + c) % _ring.Length];
                    float b = _ring[(_ringStart + (i0 + 1) * ch + c) % _ring.Length];
                    dst[written * ch + c] = a + (b - a) * t;
                }
                written++;
                pos += ratio;
            }
            int consumed = (int)pos;
            Discard(consumed * ch);
            _readPhase = pos - consumed;

            if (written < frames)
            {
                // Buffer empty: the source went silent (or is reconnecting). Pad and go idle
                // so the next sound starts with full jitter headroom instead of crackling.
                dst[(written * ch)..].Clear();
                _running = false;
                _readPhase = 0;
            }
        }
    }

    void Discard(int samples)
    {
        samples = Math.Clamp(samples, 0, _ringCount);
        _ringStart = (_ringStart + samples) % _ring.Length;
        _ringCount -= samples;
    }

    public void Dispose()
    {
        _stop = true;
        try { _pipe.Dispose(); } catch { }
        _thread?.Join(500);
    }
}
