namespace ReplayRecorder.Core;

/// <summary>Tiny append-only file logger (one file per day, kept 7 days).</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _name;

    public static void Init(string name)
    {
        _name = name;
        try
        {
            Directory.CreateDirectory(Paths.LogFolder);
            foreach (var f in Directory.GetFiles(Paths.LogFolder, "*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch { }
    }

    public static string CurrentFile => Path.Combine(Paths.LogFolder, $"{_name ?? "replayrecorder"}-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine); } catch { }
        }
    }
}
