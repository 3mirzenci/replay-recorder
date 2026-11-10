using System.Text.Json;

namespace ReplayRecorder.Core;

/// <summary>Non-destructive edits for one clip: trim range and track volumes.</summary>
public sealed class ClipEdit
{
    /// <summary>Trim start in seconds (0 = start of clip).</summary>
    public double In { get; set; }
    /// <summary>Trim end in seconds (0 or less = end of clip).</summary>
    public double Out { get; set; }
    public double GameVolumeDb { get; set; }
    public double MicVolumeDb { get; set; }
    /// <summary>Starred in the library.</summary>
    public bool Favorite { get; set; }

    public bool IsTrimmed(double duration) => In > 0.01 || (Out > 0 && Out < duration - 0.01);
    public double EffectiveOut(double duration) => Out > 0 ? Math.Min(Out, duration) : duration;
}

/// <summary>
/// Stores edits in %LOCALAPPDATA%\Replay Recorder\cache\edits.json, keyed by clip path.
/// Clips are never rewritten; the export step applies these settings.
/// </summary>
public static class ClipEdits
{
    static readonly object Gate = new();
    static Dictionary<string, ClipEdit>? _all;
    static string File_ => Path.Combine(Paths.CacheFolder, "edits.json");

    public static ClipEdit Get(string clipPath)
    {
        lock (Gate)
        {
            Load();
            return _all!.TryGetValue(clipPath, out var e)
                ? new ClipEdit { In = e.In, Out = e.Out, GameVolumeDb = e.GameVolumeDb, MicVolumeDb = e.MicVolumeDb, Favorite = e.Favorite }
                : new ClipEdit();
        }
    }

    public static void Set(string clipPath, ClipEdit edit)
    {
        lock (Gate)
        {
            Load();
            _all![clipPath] = edit;
            Save();
        }
    }

    /// <summary>Keeps a clip's edits (and star) when the file is renamed.</summary>
    public static void Move(string oldPath, string newPath)
    {
        lock (Gate)
        {
            Load();
            if (!_all!.Remove(oldPath, out var e)) return;
            _all[newPath] = e;
            Save();
        }
    }

    /// <summary>Drops the cached copy, so the next call reads what the library saved meanwhile.</summary>
    public static void Reload() { lock (Gate) _all = null; }

    public static void Remove(string clipPath)
    {
        lock (Gate)
        {
            Load();
            if (_all!.Remove(clipPath)) Save();
        }
    }

    static void Load()
    {
        if (_all != null) return;
        try
        {
            if (File.Exists(File_))
                _all = JsonSerializer.Deserialize<Dictionary<string, ClipEdit>>(File.ReadAllText(File_));
        }
        catch (Exception ex)
        {
            // Keep the unreadable file: starting fresh would overwrite every favorite and trim on the next save.
            // Dated, so a second corruption can't overwrite the first backup.
            var bad = $"{File_}.{DateTime.Now:yyyyMMdd-HHmmss}.bad";
            Log.Warn($"Edits file unreadable, kept as {Path.GetFileName(bad)}: {ex.Message}");
            try { File.Move(File_, bad, true); } catch { }
        }
        _all = new Dictionary<string, ClipEdit>(_all ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.CacheFolder);
            var tmp = File_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, File_, true);
        }
        catch (Exception ex) { Log.Warn($"Saving edits failed: {ex.Message}"); }
    }
}
