using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ReplayRecorder.Core;

/// <summary>
/// Saves the trimmed range of a clip as a new clip next to the original (the original is only read).
/// The video is re-encoded so the cut lands on the exact frame (a stream copy can only start on a keyframe,
/// up to a few seconds early) at a quality a notch above the recording's, so it looks the same. Every audio
/// track (mix, game, mic, Discord) is kept as its own track, so the new clip keeps its separate volume sliders.
/// </summary>
public static class ClipTrimmer
{
    // The recorder's date stamp (and the " (recording)" / " (2)" it may add): auto-delete only removes names with it.
    static readonly Regex StampSuffix = new(@"\s*\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}( \(recording\))?( \(\d+\))?$", RegexOptions.Compiled);

    /// <summary>
    /// "Valorant 2026-11-19 14-03-22" -> "Valorant trimmed"; a renamed "big fight" -> "big fight trimmed".
    /// No date stamp, so auto-delete leaves it alone, like a clip the user renamed.
    /// </summary>
    public static string TrimmedName(string title, string game)
    {
        var name = StampSuffix.Replace(title, "").Trim();
        if (name.Length == 0) name = game;
        return "{0} trimmed".T(Paths.SafeName(name)); // SafeName cuts long names: keep the "trimmed" on the end
    }

    public static async Task<string> SaveAsync(ClipInfo clip, ClipEdit edit, Settings s,
        IProgress<(double Fraction, string Status)>? progress = null, CancellationToken ct = default)
    {
        if (Ffmpeg.FfmpegPath == null) throw new InvalidOperationException(Ffmpeg.InstallHint);
        var sw = Stopwatch.StartNew();
        double start = Math.Max(0, edit.In);
        double seconds = edit.EffectiveOut(clip.Duration) - start;
        if (seconds < 0.3) throw new InvalidOperationException("The trimmed clip is too short to save.".T());

        var dir = Path.GetDirectoryName(clip.Path)!;
        var final = DiscordExporter.UniquePath(Path.Combine(dir, TrimmedName(clip.Title, clip.Game) + ".mp4"));
        var part = final + ".part"; // the library only lists .mp4, so a half-written file never shows up
        string nvenc = DiscordExporter.ProbeVideoCodec(clip.Path) == "hevc" ? "hevc_nvenc" : "h264_nvenc";
        int quality = Math.Max(10, s.Quality - 2);
        // Never cap below what the recording itself used.
        int sourceKbps = clip.Duration > 0 ? (int)(clip.SizeBytes * 8 / clip.Duration / 1000) : 0;
        int maxKbps = Math.Max(s.MaxBitrateMbps * 1000, sourceKbps * 2);

        try
        {
            // NVENC with GPU decode, then NVENC with CPU decode, then x264: the same fallbacks as the Discord export.
            foreach (var (encoder, gpuDecode) in new[] { (nvenc, true), (nvenc, false), ("libx264", false) })
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((0, "Saving trimmed clip…".T()));
                var args = Args(clip, start, seconds, encoder, gpuDecode, quality, maxKbps, part);
                int code = await DiscordExporter.RunFfmpeg(args, seconds, f => progress?.Report((f, "Saving trimmed clip…".T())), ct);
                if (code == 0 && File.Exists(part) && new FileInfo(part).Length > 0)
                {
                    File.Move(part, final);
                    // Dated like the original moment, so it sorts next to it rather than as today's newest clip.
                    try { File.SetLastWriteTime(final, clip.Created.AddSeconds(start)); } catch { }
                    Log.Info($"Saved trimmed clip {final} ({encoder}, {seconds:0.0}s) in {sw.Elapsed.TotalSeconds:0.0}s");
                    progress?.Report((1, "Done".T()));
                    return final;
                }
                DiscordExporter.TryDelete(part);
                Log.Warn($"Save trimmed clip: {encoder}{(gpuDecode ? " (GPU decode)" : "")} failed, trying the next way");
            }
            throw new InvalidOperationException("Saving failed. See the log for details.".T());
        }
        catch
        {
            DiscordExporter.TryDelete(part);
            throw;
        }
    }

    static List<string> Args(ClipInfo clip, double start, double seconds, string encoder, bool gpuDecode, int quality, int maxKbps, string output)
    {
        var F = DiscordExporter.F;
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-nostdin", "-progress", "pipe:1", "-nostats" };
        if (gpuDecode) a.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
        a.AddRange(new[] { "-ss", F(start), "-t", F(seconds), "-i", clip.Path, "-map", "0:v:0", "-map", "0:a?" });
        int gop = Math.Max(1, (int)Math.Round(clip.Fps > 0 ? clip.Fps : 60)) * 2;
        if (encoder == "libx264")
            a.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", quality.ToString(), "-pix_fmt", "yuv420p",
                "-maxrate", $"{maxKbps}k", "-bufsize", $"{maxKbps * 2}k", "-g", gop.ToString() });
        else
        {
            a.AddRange(new[] { "-c:v", encoder, "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-cq", quality.ToString(), "-b:v", "0",
                "-maxrate", $"{maxKbps}k", "-bufsize", $"{maxKbps * 2}k", "-spatial-aq", "1", "-rc-lookahead", "32", "-bf", "3", "-g", gop.ToString() });
            if (encoder == "h264_nvenc") a.AddRange(new[] { "-profile:v", "high" });
        }
        // Audio is re-encoded too: copied audio would start on the video keyframe, up to 2 s before the cut.
        a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "mp4", output });
        return a;
    }
}
