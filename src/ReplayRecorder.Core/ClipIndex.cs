using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReplayRecorder.Core;

/// <summary>One clip on disk plus its probed metadata. The file itself is never modified.</summary>
public sealed class ClipInfo
{
    public required string Path { get; init; }
    public required string Game { get; init; }
    public required DateTime Created { get; init; }
    public long SizeBytes { get; init; }
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public int AudioTracks { get; set; }
    public string? ThumbPath { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Indices (0-based among audio streams) of the separate game and mic tracks, if any.</summary>
    public (int Game, int Mic)? SeparateTracks => AudioTracks switch
    {
        >= 3 => (1, 2), // Replay Recorder layout: 0 = mix, 1 = game + chat, 2 = mic
        2 => (0, 1),    // early Replay Recorder clips: 0 = game + chat, 1 = mic
        _ => null,
    };
}

/// <summary>
/// Scans the clips folder, probes clips with ffprobe and caches the results (and thumbnails)
/// under %LOCALAPPDATA%\Replay Recorder\cache so the library opens instantly next time.
/// </summary>
public sealed class ClipIndex
{
    sealed record CacheEntry(long Size, long Mtime, double Duration, int Width, int Height, double Fps, int AudioTracks);

    readonly string _root;
    readonly ConcurrentDictionary<string, CacheEntry> _cache;
    readonly SemaphoreSlim _probeGate = new(3);
    readonly SemaphoreSlim _thumbGate = new(2);
    readonly SemaphoreSlim _storyboardGate = new(1);
    static string CacheFile => System.IO.Path.Combine(Paths.CacheFolder, "index.json");

    static readonly Regex DateInName = new(@"(\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})", RegexOptions.Compiled);

    public ClipIndex(string clipsRoot)
    {
        _root = clipsRoot;
        _cache = LoadCache();
    }

    public string Root => _root;

    /// <summary>Lists clips (fast: no probing). Call <see cref="ProbeAsync"/> for metadata.</summary>
    public List<ClipInfo> Scan()
    {
        if (!Directory.Exists(_root)) return new();
        return Directory.EnumerateFiles(_root, "*.mp4", SearchOption.AllDirectories)
            .Select(TryCreate).Where(c => c != null).Select(c => c!)
            .OrderByDescending(c => c.Created).ToList();
    }

    public ClipInfo? TryCreate(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) return null;
            var dir = fi.Directory!.FullName;
            var game = string.Equals(System.IO.Path.TrimEndingDirectorySeparator(dir),
                System.IO.Path.TrimEndingDirectorySeparator(_root), StringComparison.OrdinalIgnoreCase)
                ? "Other" : System.IO.Path.GetRelativePath(_root, dir).Split(System.IO.Path.DirectorySeparatorChar)[0];
            var m = DateInName.Match(fi.Name);
            var created = m.Success && DateTime.TryParseExact(m.Value, "yyyy-MM-dd HH-mm-ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : fi.LastWriteTime;

            var clip = new ClipInfo { Path = fi.FullName, Game = game, Created = created, SizeBytes = fi.Length };
            if (_cache.TryGetValue(fi.FullName, out var e) && e.Size == fi.Length && e.Mtime == fi.LastWriteTimeUtc.Ticks)
            {
                clip.Duration = e.Duration; clip.Width = e.Width; clip.Height = e.Height;
                clip.Fps = e.Fps; clip.AudioTracks = e.AudioTracks;
            }
            var thumb = ThumbFile(fi);
            if (File.Exists(thumb)) clip.ThumbPath = thumb;
            return clip;
        }
        catch { return null; }
    }

    public bool NeedsProbe(ClipInfo c) => c.Duration <= 0;

    /// <summary>Fills in duration, resolution, fps and audio track count via ffprobe.</summary>
    public async Task ProbeAsync(ClipInfo c, CancellationToken ct = default)
    {
        if (Ffmpeg.FfprobePath == null) return;
        await _probeGate.WaitAsync(ct);
        try
        {
            var (code, json) = await RunStdout(Ffmpeg.FfprobePath, new[]
            {
                "-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height,avg_frame_rate",
                "-of", "json", c.Path,
            }, ct);
            if (code != 0) return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            c.Duration = root.TryGetProperty("format", out var f2) && f2.TryGetProperty("duration", out var d2)
                && double.TryParse(d2.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? dv : 0;
            int audio = 0;
            foreach (var s in root.GetProperty("streams").EnumerateArray())
            {
                var type = s.GetProperty("codec_type").GetString();
                if (type == "audio") audio++;
                else if (type == "video" && c.Width == 0)
                {
                    c.Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    c.Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    var fr = s.TryGetProperty("avg_frame_rate", out var r) ? r.GetString() ?? "0/1" : "0/1";
                    var parts = fr.Split('/');
                    if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
                        && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var dn) && dn > 0) c.Fps = n / dn;
                }
            }
            c.AudioTracks = audio;
            var fi = new FileInfo(c.Path);
            _cache[c.Path] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, c.Duration, c.Width, c.Height, c.Fps, c.AudioTracks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Probe failed for {c.Path}: {ex.Message}"); }
        finally { _probeGate.Release(); }
    }

    /// <summary>Creates the cached thumbnail (a frame near the end, where the highlight usually is).</summary>
    public async Task EnsureThumbnailAsync(ClipInfo c, CancellationToken ct = default)
    {
        if (c.ThumbPath != null || Ffmpeg.FfmpegPath == null) return;
        await _thumbGate.WaitAsync(ct);
        try
        {
            var fi = new FileInfo(c.Path);
            var thumb = ThumbFile(fi);
            if (!File.Exists(thumb))
            {
                Directory.CreateDirectory(Paths.ThumbFolder);
                double at = c.Duration > 0 ? Math.Max(0, c.Duration * 0.75) : 1;
                var tmp = thumb + ".tmp.jpg";
                var (code, _) = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath, new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", c.Path,
                    "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", tmp,
                }, ct: ct);
                if (code != 0 || !File.Exists(tmp)) return;
                File.Move(tmp, thumb, true);
            }
            c.ThumbPath = thumb;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Thumbnail failed for {c.Path}: {ex.Message}"); }
        finally { _thumbGate.Release(); }
    }

    public const int StoryboardFrames = 10;

    /// <summary>
    /// A strip of <see cref="StoryboardFrames"/> frames across the clip, for hover scrubbing in the
    /// grid. Made on first hover from keyframes only (fast: ~30 decodes for a minute), then cached.
    /// </summary>
    public async Task<string?> EnsureStoryboardAsync(ClipInfo c, CancellationToken ct = default)
    {
        if (Ffmpeg.FfmpegPath == null || c.Duration <= 0) return null;
        var fi = new FileInfo(c.Path);
        if (!fi.Exists) return null;
        var file = ThumbFile(fi)[..^4] + "_sb.jpg";
        if (File.Exists(file)) return file;
        await _storyboardGate.WaitAsync(ct);
        try
        {
            if (File.Exists(file)) return file;
            Directory.CreateDirectory(Paths.ThumbFolder);
            var tmp = file + ".tmp.jpg";
            string rate = (StoryboardFrames / c.Duration).ToString("0.######", CultureInfo.InvariantCulture);
            var (code, _) = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath, new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-skip_frame", "nokey", "-i", c.Path, "-an",
                "-vf", $"fps={rate},scale=320:-2,tile={StoryboardFrames}x1",
                "-frames:v", "1", "-q:v", "5", tmp,
            }, ct: ct);
            if (code != 0 || !File.Exists(tmp)) return null;
            File.Move(tmp, file, true);
            return file;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Storyboard failed for {c.Path}: {ex.Message}"); return null; }
        finally { _storyboardGate.Release(); }
    }

    public const int LevelsPerSecond = 10;

    /// <summary>
    /// Loudness of the game (+ chat) and mic tracks for the player's sound bars: RMS in dBFS,
    /// <see cref="LevelsPerSecond"/> values a second. Mic is null for single-track clips. Made once, then cached.
    /// </summary>
    public async Task<(float[] Game, float[]? Mic)?> EnsureLevelsAsync(ClipInfo c, CancellationToken ct = default)
    {
        if (Ffmpeg.FfmpegPath == null || c.AudioTracks == 0) return null;
        var fi = new FileInfo(c.Path);
        if (!fi.Exists) return null;
        var file = ThumbFile(fi)[..^4] + "_lv.bin";
        if (TryReadLevels(file) is { } cached) return cached;
        await _levelsGate.WaitAsync(ct);
        try
        {
            if (TryReadLevels(file) is { } again) return again;
            const string Mono = "aformat=sample_fmts=flt:sample_rates=48000:channel_layouts=mono";
            var tracks = c.SeparateTracks;
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-i", c.Path, "-vn" };
            if (tracks is var (g, m))
                args.AddRange(new[] { "-filter_complex", $"[0:a:{g}]{Mono}[g];[0:a:{m}]{Mono}[m];[g][m]amerge=inputs=2[out]", "-map", "[out]" });
            else
                args.AddRange(new[] { "-map", "0:a:0", "-af", Mono });
            args.AddRange(new[] { "-f", "f32le", "-" });

            var (game, mic) = await ReadLevels(args, tracks != null ? 2 : 1, ct);
            if (game == null) return null;
            Directory.CreateDirectory(Paths.ThumbFolder);
            using (var w = new BinaryWriter(File.Create(file)))
            {
                w.Write(game.Length); w.Write(mic != null);
                foreach (var v in game) w.Write(v);
                if (mic != null) foreach (var v in mic) w.Write(v);
            }
            return (game, mic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Sound levels failed for {c.Path}: {ex.Message}"); return null; }
        finally { _levelsGate.Release(); }
    }

    readonly SemaphoreSlim _levelsGate = new(1);

    static (float[] Game, float[]? Mic)? TryReadLevels(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var r = new BinaryReader(File.OpenRead(file));
            int n = r.ReadInt32(); bool hasMic = r.ReadBoolean();
            var game = new float[n]; for (int i = 0; i < n; i++) game[i] = r.ReadSingle();
            float[]? mic = null;
            if (hasMic) { mic = new float[n]; for (int i = 0; i < n; i++) mic[i] = r.ReadSingle(); }
            return (game, mic);
        }
        catch { return null; } // half-written or old format: made again
    }

    /// <summary>Runs ffmpeg writing 48 kHz float samples (1 or 2 interleaved channels) to stdout; RMS per bucket in dB.</summary>
    static async Task<(float[]? Game, float[]? Mic)> ReadLevels(List<string> args, int channels, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg.FfmpegPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        ChildProcesses.Track(p);
        try { p.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { } // a game running meanwhile goes first
        using var kill = ct.Register(() => { try { p.Kill(); } catch { } });
        _ = p.StandardError.ReadToEndAsync(ct);

        var acc = new LevelAccumulator(channels, 48000 / LevelsPerSecond);
        var buf = new byte[1 << 16];
        int carry = 0; // bytes of an incomplete float left from the last read
        var stream = p.StandardOutput.BaseStream;
        for (int n; (n = await stream.ReadAsync(buf.AsMemory(carry, buf.Length - carry), ct)) > 0;)
        {
            int total = carry + n, whole = total / 4 * 4;
            acc.Add(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buf.AsSpan(0, whole)));
            carry = total - whole;
            if (carry > 0) Buffer.BlockCopy(buf, whole, buf, 0, carry);
        }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) return (null, null);
        var lanes = acc.Finish();
        return (lanes[0], channels > 1 ? lanes[1] : null);
    }

    /// <summary>Interleaved samples in, one RMS level (dBFS) per channel per bucket out.</summary>
    public sealed class LevelAccumulator(int channels, int bucketFrames)
    {
        readonly List<float>[] _lanes = Enumerable.Range(0, channels).Select(_ => new List<float>()).ToArray();
        readonly double[] _sum = new double[channels];
        int _frames, _ch; // frames in the current bucket; channel of the next sample

        public void Add(ReadOnlySpan<float> samples)
        {
            foreach (var s in samples)
            {
                _sum[_ch] += s * s;
                if (++_ch < channels) continue;
                _ch = 0;
                if (++_frames == bucketFrames) Flush();
            }
        }

        public float[][] Finish()
        {
            if (_frames > 0) Flush();
            return _lanes.Select(l => l.ToArray()).ToArray();
        }

        void Flush()
        {
            for (int c = 0; c < channels; c++)
            {
                _lanes[c].Add((float)(10 * Math.Log10(Math.Max(_sum[c] / _frames, 1e-10))));
                _sum[c] = 0;
            }
            _frames = 0;
        }
    }

    /// <summary>
    /// Deletes thumbnails and hover strips whose clip is gone (deleted, renamed or moved): they're
    /// keyed by path, size and date, so nothing else ever removes them.
    /// </summary>
    public void CleanupThumbnails()
    {
        try
        {
            if (!Directory.Exists(Paths.ThumbFolder) || !Directory.Exists(_root)) return;
            var keep = Directory.EnumerateFiles(_root, "*.mp4", SearchOption.AllDirectories)
                .Select(p => System.IO.Path.GetFileNameWithoutExtension(ThumbFile(new FileInfo(p))))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (var f in Directory.EnumerateFiles(Paths.ThumbFolder))
            {
                var name = System.IO.Path.GetFileName(f);
                var hash = name.Length >= 16 ? name[..16] : name; // "<hash>.jpg", "<hash>_sb.jpg", "<hash>_sb.jpg.tmp.jpg"
                if (keep.Contains(hash)) continue;
                try { File.Delete(f); removed++; } catch { }
            }
            if (removed > 0) Log.Info($"Removed {removed} thumbnails of clips that no longer exist");
        }
        catch (Exception ex) { Log.Warn($"Thumbnail cleanup: {ex.Message}"); }
    }

    static string ThumbFile(FileInfo fi)
    {
        var key = $"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return System.IO.Path.Combine(Paths.ThumbFolder, hash + ".jpg");
    }

    public void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Paths.CacheFolder);
            // Drop entries for files that no longer exist.
            var live = _cache.Where(kv => File.Exists(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(live));
        }
        catch (Exception ex) { Log.Warn($"Saving clip cache failed: {ex.Message}"); }
    }

    static ConcurrentDictionary<string, CacheEntry> LoadCache()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CacheFile));
                if (d != null) return new(d, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    static async Task<(int, string)> RunStdout(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        ChildProcesses.Track(p);
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await outTask);
    }
}
