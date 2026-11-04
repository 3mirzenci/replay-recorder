using System.Diagnostics;
using System.Globalization;
using ReplayRecorder.Core;
using ReplayRecorder.Recorder.Audio;

namespace ReplayRecorder.Recorder;

/// <summary>
/// The instant-replay pipeline.
///
///   screen --(Windows Graphics Capture, GPU texture)--> NVENC --> 2 s MPEG-TS segments on disk
///   speakers (WASAPI loopback) --pipe--> AAC track 2   \  track 1 = both mixed,
///   microphone (WASAPI)        --pipe--> AAC track 3   /  all muxed into the same segments
///
/// Video frames never leave the GPU. Old segments are deleted as new ones arrive, so disk use
/// stays at roughly (buffer length x bitrate). Saving a clip just joins the newest segments
/// with stream copy (no re-encode), so it is near-instant and costs almost no CPU, and the
/// recording keeps running untouched while it happens.
///
/// Sync: every video frame is placed at its capture time mapped to wall-clock time minus the
/// epoch (see BuildArgs), and the audio sinks write sample N for wall-clock epoch + N/48000.
/// With -copyts all streams share one timeline. Constant frame rate comes from the fps filter.
///
/// RAM mode (Settings.BufferInRam): ffmpeg writes one continuous MPEG-TS stream to stdout instead,
/// and PumpTs cuts it into the same 2 s segments in memory at each video keyframe. Nothing touches
/// the disk until a clip is saved (or a full recording runs, whose segments are spilled to files).
/// </summary>
sealed class ReplayEngine : IDisposable
{
    const double SegmentSeconds = 2.0;

    // Tuned on a HyperX Cloud III (+5 dB boost); loudness range of quiet talk to shouting, raw 19 LU:
    // Light 15 (same overall loudness as raw), Medium 11 (whispers stay ~10 dB under normal talk),
    // Strong 7 (whisper ~ shout). The gate sits between quiet talk (~-45 dBFS) and background music
    // bleed (~-62), so pauses stay quiet. Only the limiter looks ahead (5 ms): A/V sync is unaffected.
    static string MicLevelingChain(MicLeveling m)
    {
        var comp = m switch
        {
            MicLeveling.Light => "threshold=-22dB:ratio=2.5:makeup=5dB",
            MicLeveling.Strong => "threshold=-40dB:ratio=5:makeup=24dB",
            _ => "threshold=-28dB:ratio=3:makeup=10dB",
        };
        return "highpass=f=80,agate=threshold=-50dB:range=-40dB:ratio=4:attack=5:release=300," +
               $"acompressor={comp}:knee=4:attack=10:release=250,alimiter=limit=0.89:attack=5:release=50:level=0";
    }

    Settings _s;
    Process? _ffmpeg;
    ClockedAudioSink? _sysSink, _micSink;
    WasapiSource? _sysSrc, _micSrc;
    string _runDir = "";
    long _epochTicks;        // DateTime.UtcNow.Ticks at timeline zero
    readonly List<Segment> _segments = new();
    readonly object _segLock = new();
    long _segListPos;
    System.Threading.Timer? _housekeeping;
    readonly SemaphoreSlim _saveLock = new(1, 1);
    volatile bool _stopping;
    int _restartCount;
    DateTime _lastStart;
    double? _recordingStart; // timeline seconds; segments after this are kept until recording stops
    string _recordingGame = "";

    public bool Running => _ffmpeg is { HasExited: false };
    public bool IsRecording => _recordingStart != null;
    public DisplayInfo? Display { get; private set; }
    public string StatusText { get; private set; } = "Stopped".T();
    public event Action? StatusChanged;
    public event Action<string>? Problem;

    /// <summary>A 2 s piece of the stream: a file on disk, or (RAM mode) bytes in memory.</summary>
    sealed record Segment(string? Path, double Start, double End, byte[]? Data = null)
    {
        public bool Available => Data != null || (Path != null && File.Exists(Path));
    }

    bool _ram;
    bool _wgc; // Settings.Capture, unless this ffmpeg is too old for it
    byte[]? _tsHeader; // PAT + PMT packets, prepended when joining in-memory segments

    public ReplayEngine(Settings settings) => _s = settings;

    double Now => (DateTime.UtcNow.Ticks - _epochTicks) / (double)TimeSpan.TicksPerSecond;

    // ------------------------------------------------------------------ start / stop

    public void Start()
    {
        _stopping = false;
        if (Ffmpeg.FfmpegPath == null) { Fail(Ffmpeg.InstallHint.T()); return; }

        Display = Displays.Pick(_s.Monitor);
        if (Display == null) { Fail("No monitor found to capture.".T()); return; }

        var encoder = ChooseEncoder(Display);
        _wgc = _s.Capture == CaptureMethod.Wgc;
        if (_wgc && !Ffmpeg.HasGfxCapture)
        {
            _wgc = false;
            Log.Warn($"ffmpeg at {Ffmpeg.FfmpegPath} has no gfxcapture (needs 8.0+); using Desktop Duplication");
            Problem?.Invoke("Your ffmpeg is too old for Windows Graphics Capture, so Desktop Duplication is used instead. To update: open Terminal and run  winget upgrade Gyan.FFmpeg  then restart Replay Recorder.".T());
        }
        if (Display.HdrEnabled)
        {
            // Capture is 8-bit SDR: Windows maps the HDR desktop down, so SDR content looks right
            // but bright HDR highlights clip. Recording real HDR (10-bit HEVC) is not supported yet.
            Log.Warn($"{Display.DeviceName} has Windows HDR on; clips are recorded in SDR and HDR highlights may clip");
            Problem?.Invoke("HDR is on for this monitor. Clips are recorded in SDR, so very bright highlights may look clipped. Turn HDR off in Windows display settings for the most accurate colours.".T());
        }
        Directory.CreateDirectory(_s.BufferFolder);
        CleanOldRuns();
        _runDir = Path.Combine(_s.BufferFolder, $"run_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(_runDir);
        lock (_segLock) { _segments.Clear(); _segListPos = 0; }

        // Audio sinks first: their pipes must exist before ffmpeg opens them.
        var tag = Guid.NewGuid().ToString("N"); // unguessable pipe names
        _sysSink = new ClockedAudioSink($"replayrecorder_sys_{tag}", 2) { Gain = DbToGain(_s.SystemVolumeDb), Muted = !_s.SystemAudioEnabled };
        _micSink = new ClockedAudioSink($"replayrecorder_mic_{tag}", 1) { Gain = DbToGain(_s.MicVolumeDb), Muted = !_s.MicEnabled };

        _ram = _s.BufferInRam;
        _tsHeader = null;
        _epochTicks = DateTime.UtcNow.Ticks;
        var args = BuildArgs(Display, encoder);
        Log.Info($"Starting capture of {Display} with {encoder}, buffer in {(_ram ? "RAM" : "files")}");
        Log.Info("ffmpeg " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        var psi = new ProcessStartInfo(Ffmpeg.FfmpegPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = _ram,
            WorkingDirectory = _runDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            _ffmpeg = Process.Start(psi)!;
            ChildProcesses.Track(_ffmpeg);
            // Above normal so a CPU-heavy game can't starve the encoder feed; it uses ~1-2 % CPU.
            try { _ffmpeg.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            _ffmpeg.EnableRaisingEvents = true;
            _ffmpeg.Exited += OnFfmpegExited;
            var ff = _ffmpeg;
            _ = Task.Run(() => RaiseGpuPriority(ff));
            _ = Task.Run(() => PumpStderr(ff));
            if (_ram) _ = Task.Run(() => PumpTs(ff));
        }
        catch (Exception ex)
        {
            Fail("Could not start ffmpeg: {0}".T(ex.Message));
            DisposeAudio();
            return;
        }

        _sysSink.Start(_epochTicks);
        _micSink.Start(_epochTicks);
        _sysSrc = new WasapiSource(loopback: true, _s.SystemAudioDeviceId, _sysSink);
        _micSrc = new WasapiSource(loopback: false, _s.MicDeviceId, _micSink);
        _sysSrc.Start();
        _micSrc.Start();

        _lastStart = DateTime.UtcNow;
        _housekeeping = new System.Threading.Timer(_ => Housekeep(), null, 1000, 1000);
        SetStatus(StatusLine());
    }

    string StatusLine() => "Recording {0} · buffer {1}s".T(Display?.DeviceName.TrimStart('\\', '.'), _s.BufferSeconds);

    public void Stop()
    {
        _stopping = true;
        _housekeeping?.Dispose();
        _housekeeping = null;
        var ff = _ffmpeg;
        _ffmpeg = null;
        if (ff != null)
        {
            ff.Exited -= OnFfmpegExited;
            try
            {
                // 'q' lets ffmpeg finish the current segment cleanly.
                ff.StandardInput.Write('q');
                ff.StandardInput.Flush();
                if (!ff.WaitForExit(3000)) ff.Kill();
            }
            catch { try { ff.Kill(); } catch { } }
            ff.Dispose();
        }
        DisposeAudio();
        SetStatus("Paused".T());
    }

    void DisposeAudio()
    {
        _sysSrc?.Dispose(); _sysSrc = null;
        _micSrc?.Dispose(); _micSrc = null;
        _sysSink?.Dispose(); _sysSink = null;
        _micSink?.Dispose(); _micSink = null;
    }

    void OnFfmpegExited(object? sender, EventArgs e)
    {
        if (_stopping) return;
        var code = (sender as Process)?.ExitCode;
        Log.Warn($"ffmpeg exited unexpectedly (code {code})");
        // Restart with backoff; give up after repeated immediate failures (bad settings).
        bool quickFail = DateTime.UtcNow - _lastStart < TimeSpan.FromSeconds(10);
        _restartCount = quickFail ? _restartCount + 1 : 0;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Stop();
            if (_restartCount >= 3)
            {
                Fail("Capture keeps failing. Check the log (tray menu > Open log).".T());
                return;
            }
            Thread.Sleep(1000 * _restartCount);
            _stopping = false;
            Start();
        });
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priorityClass);
    const int GpuPriorityHigh = 4; // D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH; the game runs at NORMAL (2)

    /// <summary>
    /// Lets ffmpeg's capture and encode work go ahead of the game on the GPU. Without it a GPU-heavy
    /// game starves capture and clips freeze or skip frames (measured in MW at ~65 % GPU).
    /// Windows only accepts it once ffmpeg has set up its graphics, so retry for a few seconds.
    /// </summary>
    static async Task RaiseGpuPriority(Process p)
    {
        int status = 0;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            try { if (p.HasExited) return; status = D3DKMTSetProcessSchedulingPriorityClass(p.Handle, GpuPriorityHigh); }
            catch (Exception ex) { Log.Warn($"GPU priority: {ex.Message}"); return; }
            if (status == 0) { Log.Info("ffmpeg GPU priority set to high"); return; }
        }
        Log.Warn($"Couldn't raise ffmpeg's GPU priority (status 0x{status:X8}); clips may skip frames in GPU-heavy games");
    }

    async Task PumpStderr(Process p)
    {
        try
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
                if (line.Length > 0) Log.Info("[ffmpeg] " + line);
        }
        catch { }
    }

    // ------------------------------------------------------------------ ffmpeg command line

    string ChooseEncoder(DisplayInfo d)
    {
        var e = _s.Encoder;
        if (e == EncoderBackend.Auto)
        {
            if (d.OnNvidia) e = EncoderBackend.Nvenc;
            else
            {
                // Hybrid/Optimus: this screen is driven by the Intel iGPU, so its frames live on
                // the Intel GPU. Encode there with Quick Sync instead of copying across GPUs.
                Log.Warn($"{d.DeviceName} is driven by \"{d.AdapterName}\", not the NVIDIA GPU. " +
                         "Using Intel Quick Sync. For best results switch the MUX to discrete-GPU mode.");
                Problem?.Invoke("{0} is on {1}: using Intel Quick Sync instead of NVENC.".T(d.DeviceName, d.AdapterName));
                e = d.OnIntel ? EncoderBackend.Qsv : EncoderBackend.X264;
            }
        }
        return e switch
        {
            EncoderBackend.Nvenc => _s.Codec == VideoCodec.Hevc ? "hevc_nvenc" : "h264_nvenc",
            EncoderBackend.Qsv => _s.Codec == VideoCodec.Hevc ? "hevc_qsv" : "h264_qsv",
            _ => "libx264",
        };
    }

    (int W, int H) OutputSize(DisplayInfo d)
    {
        int h = _s.Height > 0 ? _s.Height : Math.Min(d.Height, _s.MaxNativeHeight);
        if (h >= d.Height) return (d.Width, d.Height);
        int w = (int)Math.Round(d.Width * (double)h / d.Height / 2) * 2;
        return (w, h & ~1);
    }

    List<string> BuildArgs(DisplayInfo d, string encoder)
    {
        int fps = _s.Fps;
        int gop = (int)(fps * SegmentSeconds);
        var (w, h) = OutputSize(d);
        bool scale = w != d.Width || h != d.Height;
        long epochUs = (_epochTicks - DateTime.UnixEpoch.Ticks) / 10;
        string I(double v) => v.ToString(CultureInfo.InvariantCulture);

        string src;
        if (_wgc)
        {
            // Capture at the monitor's refresh rate, not the output rate: WGC can only deliver on
            // refresh ticks, so a 60 fps cap on a 144 Hz screen rounds to every 3rd tick = 48 fps,
            // and the fps filter then has to duplicate ~12 frames a second (visible stutter).
            // Capturing every tick lets the fps filter pick the frame nearest each 1/60 s slot;
            // the extra frames are dropped on the GPU before encoding.
            int captureRate = Math.Clamp(d.RefreshHz > 0 ? d.RefreshHz : fps * 2, fps, 240);
            src = $"gfxcapture=hmonitor={d.HMonitor}:max_framerate={captureRate}:capture_cursor={(_s.CaptureCursor ? 1 : 0)}";
            if (scale) src += $":width={w}:height={h}:resize_mode=scale_aspect:scale_mode=bicubic";
        }
        else
        {
            // Desktop Duplication: native resolution only (the D3D11 scaler is broken in current ffmpeg builds).
            int outputIdx = Displays.Enumerate().Where(x => x.AdapterName == d.AdapterName)
                .OrderBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase).ToList().FindIndex(x => x.DeviceName == d.DeviceName);
            src = $"ddagrab=output_idx={Math.Max(0, outputIdx)}:framerate={fps}:draw_mouse={(_s.CaptureCursor ? 1 : 0)}";
        }
        // Put each frame on the audio timeline (wall clock - epoch) using its *capture* time, not
        // the time it happens to pass through this filter: ffmpeg's graph can stall briefly, and
        // stamping on arrival turned those stalls into bunched frames (stutter). The capture
        // clock (QPC) is mapped to the wall clock with a running minimum of (wall - capture):
        // the least-delayed frames reveal the true offset, delayed ones are ignored, and the
        // +10 us/frame creep lets it follow slow drift between the two clocks.
        //   ld(1) = this frame's (wall - capture) in us;  ld(0) = offset estimate
        var anchor = "setpts='st(1,RTCTIME-PTS*TB*1000000);" +
                     "st(0,if(eq(ld(0),0),ld(1),min(ld(0)+10,ld(1))));" +
                     $"(PTS*TB*1000000+ld(0)-{epochUs})/(1000000*TB)'";
        var video = $"{src},{anchor},fps={fps}";
        if (encoder == "libx264") video += ",hwdownload,format=bgra";
        video += "[v]";

        // Separate graph (own thread) so audio mixing can never hold up video frames.
        // Track 1 mixes game + mic so ordinary players (which only play the first track) have
        // everything; tracks 2 and 3 keep them separate so the library can rebalance them.
        // Mic leveling runs once, then feeds both the mix (track 1) and the mic track (track 3).
        bool level = _s.MicLevelingStrength != MicLeveling.Off;
        var mic = level ? $"[1:a]{MicLevelingChain(_s.MicLevelingStrength)},asplit[miclv][mic];[miclv]" : "[1:a]";
        var audio = mic + "pan=stereo|c0=c0|c1=c0[micst];[0:a][micst]amix=inputs=2:normalize=0:duration=longest," +
                    // Game and voice peaking together would clip the sum; a transparent limiter catches it.
                    "alimiter=limit=0.95:attack=5:release=50:level=0[mix]";

        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats",
            "-filter_threads", "1", "-filter_complex_threads", "1",
            "-f", "f32le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "4096", "-i", _sysSink!.PipePath,
            "-f", "f32le", "-ar", "48000", "-ac", "1", "-thread_queue_size", "4096", "-i", _micSink!.PipePath,
            "-filter_complex", video,
            "-filter_complex", audio,
            "-map", "[v]", "-map", "[mix]", "-map", "0:a", "-map", level ? "[mic]" : "1:a",
        };

        switch (encoder)
        {
            case "h264_nvenc":
            case "hevc_nvenc":
                a.AddRange(new[]
                {
                    "-c:v", encoder, "-preset", "p4", "-tune", "hq",
                    "-rc", "vbr", "-cq", I(_s.Quality), "-b:v", "0",
                    "-maxrate", $"{_s.MaxBitrateMbps}M", "-bufsize", $"{_s.MaxBitrateMbps * 2}M",
                    "-rc-lookahead", "0", "-forced-idr", "1",
                    // No B-frames and a minimal surface pool: B-frames double NVENC's frame pool
                    // (measured ~280 MB -> ~110 MB working set) for a few % better compression,
                    // which doesn't matter at recording bitrates.
                    "-bf", "0", "-surfaces", "3", "-delay", "0",
                });
                if (encoder == "h264_nvenc") a.AddRange(new[] { "-profile:v", "high" });
                break;
            case "h264_qsv":
            case "hevc_qsv":
                a.AddRange(new[] { "-c:v", encoder, "-preset", "medium", "-global_quality", I(_s.Quality + 2),
                    "-maxrate", $"{_s.MaxBitrateMbps}M", "-look_ahead", "0" });
                break;
            default:
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", I(_s.Quality + 2),
                    "-maxrate", $"{_s.MaxBitrateMbps}M", "-bufsize", $"{_s.MaxBitrateMbps * 2}M", "-pix_fmt", "yuv420p" });
                break;
        }

        a.AddRange(new[]
        {
            // Fixed GOP = segment length, so every segment starts on a keyframe.
            "-g", I(gop), "-keyint_min", I(gop),
            "-force_key_frames", $"expr:gte(t,n_forced*{I(SegmentSeconds)})",
        });

        if (encoder.Contains("nvenc"))
        {
            // NVENC converts the RGB capture to YUV with the BT.601 matrix (measured), so tag it
            // that way; untagged/mis-tagged files are what make clips look too dark or washed out.
            a.AddRange(new[] { "-colorspace", "smpte170m", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv" });
        }
        else
        {
            a.AddRange(new[] { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv" });
        }

        a.AddRange(new[]
        {
            "-c:a", "aac", "-b:a:0", "192k", "-b:a:1", "192k", "-b:a:2", "128k",
            "-copyts",
            "-max_muxing_queue_size", "4096",
        });
        if (_ram)
            // No mux delay: PES timestamps then equal the timeline, which PumpTs reads as segment times.
            a.AddRange(new[] { "-f", "mpegts", "-muxdelay", "0", "-muxpreload", "0", "pipe:1" });
        else
            a.AddRange(new[]
            {
                "-f", "segment", "-segment_time", I(SegmentSeconds), "-segment_format", "mpegts",
                "-segment_list", "segments.csv", "-segment_list_type", "csv",
                "-reset_timestamps", "0",
                "seg_%08d.ts",
            });
        return a;
    }

    // ------------------------------------------------------------------ segment ring buffer

    void Housekeep()
    {
        try
        {
            if (!_ram) ReadSegmentList();
            lock (_segLock)
            {
                if (_segments.Count == 0) return;
                double newest = _segments[^1].End;
                double keepFrom = newest - _s.BufferSeconds - 3 * SegmentSeconds;
                if (_recordingStart is double rs) keepFrom = Math.Min(keepFrom, rs - SegmentSeconds);
                while (_segments.Count > 0 && _segments[0].End < keepFrom && _saveLock.CurrentCount > 0)
                {
                    if (_segments[0].Path is { } p) TryDelete(p);
                    _segments.RemoveAt(0); // RAM mode: the bytes go with it
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Housekeeping: {ex.Message}"); }
    }

    void ReadSegmentList()
    {
        var file = Path.Combine(_runDir, "segments.csv");
        if (!File.Exists(file)) return;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length <= _segListPos) return;
        fs.Seek(_segListPos, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        var text = sr.ReadToEnd();
        int lastNl = text.LastIndexOf('\n');
        if (lastNl < 0) return; // partial line; read it next time
        _segListPos += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNl + 1));
        lock (_segLock)
        {
            foreach (var line in text[..lastNl].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(',');
                if (parts.Length < 3) continue;
                if (!double.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
                    !double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)) continue;
                var name = string.Join(',', parts[..^2]).Trim('"');
                _segments.Add(new Segment(Path.Combine(_runDir, name), start, end));
            }
        }
    }

    // ------------------------------------------------------------------ RAM mode: split the TS stream

    const int VideoPid = 0x100; // ffmpeg's mpegts muxer numbers streams from 0x100; video is mapped first
    const int PmtPid = 0x1000;

    /// <summary>
    /// Reads ffmpeg's MPEG-TS stdout and cuts it into segments at video keyframes (packets with the
    /// random-access flag), timed by the keyframe's PTS. Must keep reading: if it stops, ffmpeg blocks.
    /// </summary>
    async Task PumpTs(Process p)
    {
        var stream = p.StandardOutput.BaseStream;
        var buf = new byte[188 * 1024];
        int fill = 0;
        MemoryStream? cur = null;
        double curStart = 0;
        int lastSize = 4 << 20;
        byte[]? pat = null, pmt = null;
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf.AsMemory(fill, buf.Length - fill))) > 0)
            {
                fill += n;
                int i = 0;
                while (fill - i >= 188)
                {
                    if (buf[i] != 0x47) { i++; continue; } // lost sync: scan for the next packet
                    var pkt = buf.AsSpan(i, 188);
                    i += 188;
                    int pid = ((pkt[1] & 0x1F) << 8) | pkt[2];
                    if (pid == 0 && pat == null) pat = pkt.ToArray();
                    else if (pid == PmtPid && pmt == null) pmt = pkt.ToArray();
                    if (_tsHeader == null && pat != null && pmt != null) _tsHeader = [.. pat, .. pmt];

                    if (pid == VideoPid && KeyframePts(pkt) is double t)
                    {
                        if (cur != null) { lastSize = (int)cur.Length; CloseSegment(cur, curStart, t); }
                        // Sized like the previous piece (+25 %) so it rarely has to grow and copy itself.
                        cur = new MemoryStream(Math.Max(1 << 20, lastSize + lastSize / 4));
                        curStart = t;
                    }
                    cur?.Write(pkt); // packets before the first keyframe can't be decoded; dropped
                }
                Buffer.BlockCopy(buf, i, buf, 0, fill - i);
                fill -= i;
            }
        }
        catch (Exception ex) when (!_stopping) { Log.Warn($"RAM buffer reader stopped: {ex.Message}"); }
        catch { }
    }

    /// <summary>PTS in seconds if this packet starts a keyframe's PES, else null.</summary>
    static double? KeyframePts(ReadOnlySpan<byte> pkt)
    {
        if ((pkt[1] & 0x40) == 0) return null;          // payload_unit_start
        int afc = (pkt[3] >> 4) & 3, off = 4;
        bool rai = false;
        if ((afc & 2) != 0)
        {
            int len = pkt[4];
            if (len > 0) rai = (pkt[5] & 0x40) != 0;    // random_access_indicator
            off = 5 + len;
        }
        if (!rai || (afc & 1) == 0 || off + 14 > 188) return null;
        if (pkt[off] != 0 || pkt[off + 1] != 0 || pkt[off + 2] != 1 || (pkt[off + 7] & 0x80) == 0) return null;
        long pts = ((long)(pkt[off + 9] >> 1) & 7) << 30 | (long)pkt[off + 10] << 22 |
                   (long)(pkt[off + 11] >> 1) << 15 | (long)pkt[off + 12] << 7 | (long)(pkt[off + 13] >> 1);
        return pts / 90000.0; // the timeline starts at 0, so the 33-bit wrap (26.5 h) never comes up in practice
    }

    void CloseSegment(MemoryStream data, double start, double end)
    {
        Segment seg;
        if (_recordingStart != null)
        {
            // A full recording can run for an hour: don't hold that in RAM, spill it to the run folder.
            var path = Path.Combine(_runDir, $"seg_{start * 1000:0}.ts");
            try { File.WriteAllBytes(path, data.ToArray()); seg = new Segment(path, start, end); }
            catch (Exception ex) { Log.Warn($"Spilling segment failed: {ex.Message}"); seg = new Segment(null, start, end, data.ToArray()); }
        }
        else seg = new Segment(null, start, end, data.ToArray());
        lock (_segLock) _segments.Add(seg);
    }

    // ------------------------------------------------------------------ saving

    /// <summary>A saved clip and its length (shorter than the buffer right after capture starts).</summary>
    public sealed record SavedClip(string Path, double Seconds);

    /// <summary>Saves the last BufferSeconds as a clip, or null if it failed.</summary>
    public async Task<SavedClip?> SaveClipAsync(string game)
    {
        if (!Running) return null;
        double hit = Now;
        var segs = await WaitForSegmentsAsync(hit, s => s.End > hit - _s.BufferSeconds);
        if (segs.Count == 0) return null;
        return await JoinAsync(segs, game, "", trimToSeconds: _s.BufferSeconds, endAt: hit);
    }

    public void StartRecording(string game)
    {
        if (!Running || IsRecording) return;
        _recordingStart = Now;
        _recordingGame = game;
        Log.Info($"Full recording started ({game})");
        StatusChanged?.Invoke();
    }

    public async Task<string?> StopRecordingAsync()
    {
        if (_recordingStart is not double start) return null;
        double stop = Now;
        var segs = await WaitForSegmentsAsync(stop, s => s.End > start);
        _recordingStart = null;
        StatusChanged?.Invoke();
        if (segs.Count == 0) return null;
        return (await JoinAsync(segs, _recordingGame, " (recording)", trimToSeconds: stop - start, endAt: stop))?.Path;
    }

    /// <summary>Waits until the segment containing <paramref name="t"/> is closed, then returns matching segments.</summary>
    async Task<List<Segment>> WaitForSegmentsAsync(double t, Func<Segment, bool> filter)
    {
        var deadline = DateTime.UtcNow.AddSeconds(SegmentSeconds * 2 + 1.5);
        while (true)
        {
            if (!_ram) ReadSegmentList();
            lock (_segLock)
            {
                bool covered = _segments.Count > 0 && _segments[^1].End >= t - 0.05;
                if (covered || DateTime.UtcNow > deadline)
                {
                    if (!covered) Log.Warn("Newest segment not closed in time (static screen?); saving what is on disk");
                    return _segments.Where(s => filter(s) && s.Start < t + SegmentSeconds && s.Available).ToList();
                }
            }
            await Task.Delay(100);
        }
    }

    async Task<SavedClip?> JoinAsync(List<Segment> segs, string game, string suffix, double trimToSeconds, double endAt)
    {
        await _saveLock.WaitAsync();
        try
        {
            var folder = Path.Combine(_s.ClipsFolder, Paths.SafeName(game));
            Directory.CreateDirectory(folder);
            var name = $"{Paths.SafeName(game)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}{suffix}";
            var final = Path.Combine(folder, name + ".mp4");
            for (int i = 2; File.Exists(final); i++) final = Path.Combine(folder, $"{name} ({i}).mp4");
            var part = final + ".part";

            // Segments are cut on keyframes, so the join is a pure stream copy. Start from the
            // keyframe at or just before (end - length) so the clip is at least the buffer length.
            double from = endAt - trimToSeconds;
            var first = segs.LastOrDefault(s => s.Start <= from) ?? segs[0];
            segs = segs.SkipWhile(s => s != first).ToList();

            using var concat = new ConcatStream(segs, _ram ? _tsHeader : null);
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "mpegts", "-i", "pipe:0",
                "-map", "0", "-c", "copy",
                "-t", (endAt - first.Start + 0.5).ToString("0.###", CultureInfo.InvariantCulture),
                "-avoid_negative_ts", "make_zero",
                // No +faststart: it rewrites the whole file, a disk burst mid-game; local playback and the
                // Discord export (which adds it) don't need it.
                "-metadata", $"title={game}",
                "-metadata", "comment=Replay Recorder",
                "-metadata:s:a:0", "title=Everything (game + voice chat + mic)",
                "-metadata:s:a:1", "title=Game + voice chat",
                "-metadata:s:a:2", "title=Microphone",
                "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0",
            };
            if (_s.Codec == VideoCodec.Hevc) a(args, "-tag:v", "hvc1");
            a(args, "-f", "mp4", part);

            var sw = Stopwatch.StartNew();
            var (code, err) = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath!, args, concat);
            if (code != 0 || !File.Exists(part))
            {
                Log.Error($"Clip join failed (code {code}): {err}");
                TryDelete(part);
                return null;
            }
            File.Move(part, final);
            Log.Info($"Saved {final} ({segs.Count} segments, {new FileInfo(final).Length / 1048576.0:0.0} MB, {sw.ElapsedMilliseconds} ms)");
            return new SavedClip(final, Math.Min(endAt - first.Start, trimToSeconds));
        }
        finally { _saveLock.Release(); }

        static void a(List<string> l, params string[] x) => l.AddRange(x);
    }

    // ------------------------------------------------------------------ misc

    /// <summary>
    /// Switches to new settings that don't need a capture restart (see <see cref="Settings.CaptureDiffers"/>):
    /// buffer length, clips folder and volumes take effect on the running capture.
    /// </summary>
    public void UseSettings(Settings s)
    {
        _s = s;
        ApplyAudioLevels();
        if (Running) SetStatus(StatusLine());
    }

    public void ApplyAudioLevels()
    {
        if (_sysSink != null) { _sysSink.Gain = DbToGain(_s.SystemVolumeDb); _sysSink.Muted = !_s.SystemAudioEnabled; }
        if (_micSink != null) { _micSink.Gain = DbToGain(_s.MicVolumeDb); _micSink.Muted = !_s.MicEnabled; }
    }

    public (float System, float Mic) Peaks => (_sysSink?.Peak ?? 0, _micSink?.Peak ?? 0);

    static float DbToGain(double db) => (float)Math.Pow(10, db / 20);

    void CleanOldRuns()
    {
        foreach (var dir in Directory.GetDirectories(_s.BufferFolder, "run_*"))
        {
            try { Directory.Delete(dir, true); } catch { /* in use by another instance */ }
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    void SetStatus(string s) { StatusText = s; StatusChanged?.Invoke(); }

    void Fail(string message)
    {
        Log.Error(message);
        SetStatus("Error".T());
        Problem?.Invoke(message);
    }

    public void Dispose()
    {
        Stop();
        try { if (Directory.Exists(_runDir)) Directory.Delete(_runDir, true); } catch { }
    }

    /// <summary>
    /// Reads segments back to back as one stream (MPEG-TS concatenates cleanly). In-memory
    /// segments have no PAT/PMT of their own, so <paramref name="header"/> goes first.
    /// </summary>
    sealed class ConcatStream(List<Segment> segs, byte[]? header) : Stream
    {
        int _index = -1;
        Stream? _cur = header != null ? new MemoryStream(header, false) : null;

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                if (_cur == null)
                {
                    if (++_index >= segs.Count) return 0;
                    var s = segs[_index];
                    if (s.Data != null) _cur = new MemoryStream(s.Data, false);
                    else
                    {
                        try { _cur = new FileStream(s.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
                        catch (IOException) { continue; } // deleted meanwhile: skip
                    }
                }
                int n = _cur.Read(buffer, offset, count);
                if (n > 0) return n;
                _cur.Dispose();
                _cur = null;
            }
        }

        protected override void Dispose(bool disposing) { _cur?.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
