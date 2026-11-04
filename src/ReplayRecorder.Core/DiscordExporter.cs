using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ReplayRecorder.Core;

/// <summary>How one export attempt is encoded.</summary>
public sealed record ExportPlan(int Width, int Height, int Fps, int VideoKbps, int AudioKbps, string Encoder, bool StreamCopy = false)
{
    public string Describe() => StreamCopy
        ? $"{Height}p (original quality, no re-encode)"
        : $"{Height}p{Fps} · {VideoKbps / 1000.0:0.0} Mbps · {(Encoder == "h264_nvenc" ? "NVENC" : "x264")}";
}

public sealed record ExportResult(string Path, long SizeBytes, ExportPlan Plan, int Attempts, TimeSpan Elapsed);

/// <summary>
/// Turns a clip (trim + game/mic balance) into an H.264/AAC MP4 that is guaranteed to fit
/// under the Discord size target. The source clip is only ever read.
///
///   video kbps = target bits / duration - audio kbps - ~2 % container overhead
///   then pick the best resolution/fps that bitrate can carry well (quality ladder),
///   encode with NVENC (GPU decode + scale + encode) or libx264 two-pass as a fallback,
///   check the real size, and if it is over, re-encode at a lower bitrate. The file only
///   gets its final name once it is verified to be under the target.
/// </summary>
public static class DiscordExporter
{
    // Quality ladder: (height, fps, minimum video kbps for that rung to look good).
    static readonly (int H, int Fps, int MinKbps)[] Ladder =
    {
        (1440, 60, 14000),
        (1080, 60, 6000),
        (720, 60, 3000),
        (720, 30, 1500),
        (480, 30, 700),
        (360, 30, 0),
    };

    const int MaxAttempts = 4;
    const double ContainerOverhead = 0.02;

    public static long TargetBytes(Settings s) => (long)(s.DiscordTargetMB * 1_000_000);

    /// <summary>Video bitrate (kbps) that fills the target for a clip of this length.</summary>
    public static (int VideoKbps, int AudioKbps) Budget(long targetBytes, double seconds)
    {
        int audio = seconds > 180 ? 96 : 128;
        double totalKbps = targetBytes * 8.0 / seconds / 1000.0 * (1 - ContainerOverhead);
        return ((int)Math.Floor(totalKbps - audio), audio);
    }

    /// <summary>Best rung of the ladder for this bitrate, never above the source's size or frame rate.</summary>
    public static ExportPlan PlanFor(ClipInfo clip, int videoKbps, int audioKbps, string encoder)
    {
        int srcH = clip.Height > 0 ? clip.Height : 1080;
        int srcW = clip.Width > 0 ? clip.Width : srcH * 16 / 9;
        int srcFps = clip.Fps > 0 ? (int)Math.Round(clip.Fps) : 60;

        var rung = Ladder.First(r => videoKbps >= r.MinKbps && (r.H <= srcH || r == Ladder[^1]));
        int h = Math.Min(rung.H, srcH) & ~1;
        int w = (int)Math.Round(srcW * (double)h / srcH / 2) * 2;
        int fps = Math.Min(rung.Fps, srcFps);
        return new ExportPlan(w, h, fps, videoKbps, audioKbps, encoder);
    }

    /// <summary>How long an export stays: long enough to paste or drag it into Discord and send it.</summary>
    public static readonly TimeSpan ExportLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Deletes exports older than <paramref name="olderThan"/> from the app's own export folder. A folder
    /// the user picked in Settings is left alone: exports there are meant to stay.
    /// </summary>
    public static void CleanupExports(Settings s, TimeSpan olderThan)
    {
        var dir = Paths.DefaultExportFolder;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(s.ExportFolder), dir, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            try
            {
                if (DateTime.Now - File.GetLastWriteTime(f) < olderThan) continue;
                File.Delete(f);
                Log.Info($"Removed old export {Path.GetFileName(f)}");
            }
            catch { } // still uploading/open somewhere: try again next time
        }
    }

    public static async Task<ExportResult> ExportAsync(ClipInfo clip, ClipEdit edit, Settings s,
        IProgress<(double Fraction, string Status)>? progress = null, CancellationToken ct = default)
    {
        if (Ffmpeg.FfmpegPath == null) throw new InvalidOperationException(Ffmpeg.InstallHint);
        var sw = Stopwatch.StartNew();
        double start = Math.Max(0, edit.In);
        double end = edit.EffectiveOut(clip.Duration);
        double seconds = end - start;
        if (seconds < 0.3) throw new InvalidOperationException("The trimmed clip is too short to export.");

        long target = TargetBytes(s);
        Directory.CreateDirectory(s.ExportFolder);
        var final = UniquePath(Path.Combine(s.ExportFolder, Paths.SafeName(clip.Title) + ".mp4"));
        var part = final + ".part";
        int attempts = 0;

        try
        {
            // Fast path: if a plain cut of the original already fits and needs no audio changes,
            // copy it (lossless, near-instant). It starts on the keyframe at/before the trim start.
            if (CanStreamCopy(clip, edit, seconds, target))
            {
                attempts++;
                progress?.Report((0.1, "Cutting without re-encoding…"));
                var plan = new ExportPlan(clip.Width, clip.Height, (int)Math.Round(clip.Fps), 0, 0, "copy", StreamCopy: true);
                var code = await RunFfmpeg(CopyArgs(clip, start, seconds, part), seconds, null, ct);
                if (code == 0 && File.Exists(part) && new FileInfo(part).Length <= target)
                    return Finish(part, final, plan, attempts, sw, progress);
                TryDelete(part);
            }

            var (videoKbps, audioKbps) = Budget(target, seconds);
            if (videoKbps < 150)
                throw new InvalidOperationException(
                    $"{Fmt(seconds)} is too long to fit in {s.DiscordTargetMB:0.#} MB with watchable quality. " +
                    $"Trim it to under {Fmt(target * 8.0 * (1 - ContainerOverhead) / 1000 / (150 + audioKbps))}.");

            string encoder = "h264_nvenc";
            bool gpuDecode = true;
            while (attempts < MaxAttempts + 1)
            {
                ct.ThrowIfCancellationRequested();
                attempts++;
                var plan = PlanFor(clip, videoKbps, audioKbps, encoder);
                string label = attempts == 1 ? plan.Describe() : $"{plan.Describe()} (retry {attempts - 1}, smaller)";
                progress?.Report((0, $"Encoding {label}"));
                Log.Info($"Export attempt {attempts}: {plan.Describe()} for {clip.Path} [{start:0.00}-{end:0.00}]");

                int code;
                if (encoder == "h264_nvenc")
                {
                    code = await RunFfmpeg(EncodeArgs(clip, edit, start, seconds, plan, gpuDecode, part, null), seconds,
                        f => progress?.Report((f, $"Encoding {label}")), ct);
                    if (code != 0 && gpuDecode)
                    {
                        // GPU decode/scale path failed (driver, odd source): same encoder, CPU decode.
                        Log.Warn("Export: GPU decode path failed, retrying with CPU decode");
                        gpuDecode = false;
                        attempts--;
                        continue;
                    }
                    if (code != 0)
                    {
                        Log.Warn("Export: NVENC failed, falling back to libx264");
                        encoder = "libx264";
                        attempts--;
                        continue;
                    }
                }
                else
                {
                    code = await EncodeX264TwoPass(clip, edit, start, seconds, plan, part, label, progress, ct);
                    if (code != 0) throw new InvalidOperationException("Encoding failed. See the log for details.");
                }

                long size = File.Exists(part) ? new FileInfo(part).Length : 0;
                Log.Info($"Export attempt {attempts}: {size / 1e6:0.00} MB (target {target / 1e6:0.00} MB)");
                if (size > 0 && size <= target)
                    return Finish(part, final, plan with { VideoKbps = videoKbps }, attempts, sw, progress);

                // Over target: scale the video bitrate by how far off we were, with a safety margin.
                TryDelete(part);
                if (size == 0) throw new InvalidOperationException("Encoding produced no output. See the log for details.");
                double factor = (double)target / size * 0.96;
                videoKbps = (int)Math.Floor(videoKbps * Math.Min(factor, 0.97));
                if (videoKbps < 100) break;
            }
            throw new InvalidOperationException($"Couldn't get the clip under {s.DiscordTargetMB:0.#} MB. Try trimming it shorter.");
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    static ExportResult Finish(string part, string final, ExportPlan plan, int attempts, Stopwatch sw, IProgress<(double, string)>? progress)
    {
        File.Move(part, final);
        long size = new FileInfo(final).Length;
        Log.Info($"Exported {final}: {size / 1e6:0.00} MB, {plan.Describe()}, {attempts} attempt(s), {sw.Elapsed.TotalSeconds:0.0}s");
        progress?.Report((1, "Done"));
        return new ExportResult(final, size, plan, attempts, sw.Elapsed);
    }

    static bool CanStreamCopy(ClipInfo clip, ClipEdit edit, double seconds, long target)
    {
        bool defaultAudio = Math.Abs(edit.GameVolumeDb) < 0.01 && Math.Abs(edit.MicVolumeDb) < 0.01;
        if (!defaultAudio || clip.Duration <= 0 || clip.AudioTracks == 2) return false; // 2-track clips have no mixed track
        if (!string.Equals(ProbeVideoCodec(clip.Path), "h264", StringComparison.OrdinalIgnoreCase)) return false;
        double estimate = clip.SizeBytes * (seconds + 2) / clip.Duration; // +2 s: may start a keyframe early
        return estimate < target * 0.9;
    }

    static List<string> CopyArgs(ClipInfo clip, double start, double seconds, string output)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-nostdin" };
        if (start > 0) a.AddRange(new[] { "-ss", F(start) });
        a.AddRange(new[] { "-i", clip.Path, "-t", F(seconds), "-map", "0:v:0" });
        if (clip.AudioTracks > 0) a.AddRange(new[] { "-map", "0:a:0" });
        a.AddRange(new[] { "-c", "copy", "-map_metadata", "-1", "-movflags", "+faststart", "-avoid_negative_ts", "make_zero",
            "-f", "mp4", output });
        return a;
    }

    /// <summary>Single-process encode args (NVENC, or one libx264 pass when <paramref name="x264Pass"/> is set).</summary>
    static List<string> EncodeArgs(ClipInfo clip, ClipEdit edit, double start, double seconds, ExportPlan plan,
        bool gpuDecode, string output, (int Pass, string LogFile)? x264Pass)
    {
        bool scale = plan.Height != clip.Height;
        bool changeFps = clip.Fps <= 0 || Math.Abs(clip.Fps - plan.Fps) > 0.5;
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-nostdin", "-progress", "pipe:1", "-nostats" };
        if (gpuDecode) a.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
        // Input seeking: fast, and frame-accurate because we re-encode.
        a.AddRange(new[] { "-ss", F(start), "-t", F(seconds), "-i", clip.Path });

        // Video
        var vf = new List<string>();
        if (gpuDecode)
        {
            if (changeFps) vf.Add($"fps={plan.Fps}");
            vf.Add(scale ? $"scale_cuda={plan.Width}:{plan.Height}:interp_algo=lanczos:format=nv12" : "scale_cuda=format=nv12");
        }
        else
        {
            if (changeFps) vf.Add($"fps={plan.Fps}");
            if (scale) vf.Add($"scale={plan.Width}:{plan.Height}:flags=lanczos");
            vf.Add("format=yuv420p");
        }

        // Audio: mix the separate game and mic tracks with the chosen volumes into one track.
        string? audioGraph = null;
        string audioMap;
        if (clip.SeparateTracks is { } t)
        {
            audioGraph = $"[0:a:{t.Game}]volume={F(edit.GameVolumeDb)}dB[ga];" +
                         $"[0:a:{t.Mic}]volume={F(edit.MicVolumeDb)}dB,aformat=channel_layouts=stereo[ma];" +
                         "[ga][ma]amix=inputs=2:normalize=0,alimiter=limit=0.95:level=0[aout]";
            audioMap = "[aout]";
        }
        else if (clip.AudioTracks > 0)
        {
            audioGraph = $"[0:a:0]volume={F(edit.GameVolumeDb)}dB,alimiter=limit=0.95:level=0[aout]";
            audioMap = "[aout]";
        }
        else audioMap = "";

        a.AddRange(new[] { "-map", "0:v:0", "-vf", string.Join(',', vf) });
        if (x264Pass is { Pass: 1 }) a.Add("-an");
        else if (audioGraph != null) a.AddRange(new[] { "-filter_complex", audioGraph, "-map", audioMap });

        int v = plan.VideoKbps;
        int gop = plan.Fps * 4;
        if (x264Pass is { } xp)
        {
            a.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "slow", "-b:v", $"{v}k", "-maxrate", $"{v * 3 / 2}k", "-bufsize", $"{v * 2}k",
                "-pass", xp.Pass.ToString(), "-passlogfile", xp.LogFile, "-profile:v", "high", "-g", gop.ToString(),
            });
        }
        else
        {
            a.AddRange(new[]
            {
                "-c:v", "h264_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-multipass", "fullres",
                "-b:v", $"{v}k", "-maxrate", $"{v * 3 / 2}k", "-bufsize", $"{v * 2}k",
                "-spatial-aq", "1", "-temporal-aq", "1", "-rc-lookahead", "32", "-bf", "3",
                "-profile:v", "high", "-g", gop.ToString(),
            });
        }

        if (x264Pass is { Pass: 1 })
        {
            a.AddRange(new[] { "-f", "null", "NUL" });
            return a;
        }
        a.AddRange(new[]
        {
            "-c:a", "aac", "-b:a", $"{plan.AudioKbps}k", "-ar", "48000", "-ac", "2",
            "-map_metadata", "-1", "-metadata", $"title={clip.Title}",
            "-movflags", "+faststart", // Discord's player can start before the whole file loads
            "-f", "mp4", output,
        });
        return a;
    }

    static async Task<int> EncodeX264TwoPass(ClipInfo clip, ClipEdit edit, double start, double seconds, ExportPlan plan,
        string output, string label, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        var log = Path.Combine(Path.GetTempPath(), $"replayrecorder-2pass-{Environment.ProcessId}");
        try
        {
            int c1 = await RunFfmpeg(EncodeArgs(clip, edit, start, seconds, plan, false, output, (1, log)), seconds,
                f => progress?.Report((f * 0.5, $"Analysing ({label})")), ct);
            if (c1 != 0) return c1;
            return await RunFfmpeg(EncodeArgs(clip, edit, start, seconds, plan, false, output, (2, log)), seconds,
                f => progress?.Report((0.5 + f * 0.5, $"Encoding {label}")), ct);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(log) + "*")) TryDelete(f);
        }
    }

    /// <summary>Runs ffmpeg, reporting progress from -progress output (out_time_us).</summary>
    static async Task<int> RunFfmpeg(List<string> args, double seconds, Action<double>? onFraction, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Ffmpeg.FfmpegPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var x in args) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        ChildProcesses.Track(p);
        var err = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            string? l;
            while ((l = await p.StandardError.ReadLineAsync()) != null) err.AppendLine(l);
        });
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            if (onFraction != null && line.StartsWith("out_time_us=") &&
                long.TryParse(line.AsSpan(12), out var us) && us > 0)
                onFraction(Math.Clamp(us / 1e6 / seconds, 0, 1));
        }
        await p.WaitForExitAsync(CancellationToken.None);
        await errTask;
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) Log.Warn($"ffmpeg export exited {p.ExitCode}: {err.ToString().Trim()}");
        return p.ExitCode;
    }

    static string? ProbeVideoCodec(string path)
    {
        if (Ffmpeg.FfprobePath == null) return null;
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg.FfprobePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var x in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", path })
                psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            ChildProcesses.Track(p);
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return o;
        }
        catch { return null; }
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}).mp4");
            if (!File.Exists(p)) return p;
        }
    }

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static string Fmt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}:{ts.Seconds:00} min" : $"{ts.Seconds} s";
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
