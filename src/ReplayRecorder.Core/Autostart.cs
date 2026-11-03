using Microsoft.Win32;

namespace ReplayRecorder.Core;

/// <summary>"Start with Windows" for the recorder, via the per-user Run key (no admin needed).</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Replay Recorder";

    /// <summary>The recorder exe; it sits next to the library exe.</summary>
    public static string RecorderPath => Path.Combine(AppContext.BaseDirectory, "ReplayRecorder.Recorder.exe");

    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) != null;
        }
    }

    public static void Set(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{RecorderPath}\"");
        else k.DeleteValue(Name, false);
    }
}
