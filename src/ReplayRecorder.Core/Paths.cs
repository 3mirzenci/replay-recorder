namespace ReplayRecorder.Core;

public static class Paths
{
    // REPLAYRECORDER_PROFILE=<folder> gives a run its own settings, cache and logs (used for stress tests,
    // so a test library never touches the real one).
    static readonly string? Profile = Environment.GetEnvironmentVariable("REPLAYRECORDER_PROFILE");

    public static string AppData { get; } = Profile != null ? Path.Combine(Profile, "roaming") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Replay Recorder");

    public static string LocalData { get; } = Profile != null ? Path.Combine(Profile, "local") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Replay Recorder");

    public static string SettingsFile => Path.Combine(AppData, "settings.json");
    public static string LogFolder => Path.Combine(LocalData, "logs");
    public static string CacheFolder => Path.Combine(LocalData, "cache");
    public static string ThumbFolder => Path.Combine(CacheFolder, "thumbs");

    public static string DefaultClipsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Replay Recorder");

    /// <summary>Discord exports live outside the clips folder so they never show up as clips.</summary>
    public static string DefaultExportFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Replay Recorder Discord");

    public static string DefaultBufferFolder => Path.Combine(LocalData, "buffer");

    /// <summary>Total size of all files under a folder (0 if it doesn't exist).</summary>
    public static long FolderSize(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch { }
        }
        return total;
    }

    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static string SafeName(string s)
    {
        var chars = s.Trim().Select(c => Invalid.Contains(c) ? '_' : c).ToArray();
        var r = new string(chars).Trim(' ', '.');
        return r.Length == 0 ? "Unknown" : r.Length > 60 ? r[..60] : r;
    }
}
