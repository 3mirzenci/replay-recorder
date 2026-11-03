using System.Text.Json;
using Microsoft.Win32;

namespace ReplayRecorder.Core;

/// <summary>
/// One-time move from the app's old name (ClipDeck): settings, cache and logs, the default clips
/// and export folders, the clip paths stored in the caches, and the autostart entry. Runs at
/// startup in whichever app starts first; every step is skipped if its target already exists, and
/// a step that fails leaves the old data where it was (nothing is deleted).
/// </summary>
public static class Migration
{
    const string OldName = "ClipDeck";
    // Logged at the end: the first log write creates the new data folder, which would block moving the old one.
    static readonly List<string> Notes = new();

    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("REPLAYRECORDER_PROFILE") != null) return; // test profiles
        using var gate = new Mutex(false, @"Local\ReplayRecorder.Migrate");
        try { gate.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { }
        try
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            MoveDir(Path.Combine(roaming, OldName), Paths.AppData);
            MoveDir(Path.Combine(local, OldName), Paths.LocalData);

            string oldClips = Path.Combine(videos, OldName), oldExports = Path.Combine(videos, OldName + " Discord");
            string oldBuffer = Path.Combine(local, OldName, "buffer");
            if (!File.Exists(Paths.SettingsFile)) { MoveAutostart(); return; }

            var s = Settings.Load();
            bool dirty = false;
            if (Same(s.ClipsFolder, oldClips) && MoveDir(oldClips, Paths.DefaultClipsFolder))
            {
                s.ClipsFolder = Paths.DefaultClipsFolder;
                RemapKeys(Path.Combine(Paths.CacheFolder, "edits.json"), oldClips, Paths.DefaultClipsFolder);
                RemapKeys(Path.Combine(Paths.CacheFolder, "index.json"), oldClips, Paths.DefaultClipsFolder);
                dirty = true;
            }
            if (Same(s.ExportFolder, oldExports) && MoveDir(oldExports, Paths.DefaultExportFolder))
            {
                s.ExportFolder = Paths.DefaultExportFolder;
                dirty = true;
            }
            if (Same(s.BufferFolder, oldBuffer)) { s.BufferFolder = Paths.DefaultBufferFolder; dirty = true; }
            if (dirty) s.Save();
            MoveAutostart();
        }
        catch (Exception ex) { Notes.Add($"Migration from {OldName}: {ex.Message}"); }
        finally { try { gate.ReleaseMutex(); } catch { } }
        foreach (var n in Notes) Log.Info("Migration: " + n);
        Notes.Clear();
    }

    static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Renames old -> new if old exists and new doesn't. True if new now holds the data.</summary>
    static bool MoveDir(string from, string to)
    {
        if (!Directory.Exists(from) || Directory.Exists(to)) return false;
        try
        {
            Directory.Move(from, to); // same drive: an instant rename
            Notes.Add($"Moved {from} -> {to}");
            return true;
        }
        catch (Exception ex)
        {
            Notes.Add($"Couldn't move {from} -> {to}: {ex.Message}; keeping the old location");
            return false;
        }
    }

    /// <summary>Caches are keyed by clip path: point them at the moved folder.</summary>
    static void RemapKeys(string file, string oldRoot, string newRoot)
    {
        try
        {
            if (!File.Exists(file)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file));
            if (d == null) return;
            var moved = d.ToDictionary(
                kv => kv.Key.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? newRoot + kv.Key[oldRoot.Length..] : kv.Key,
                kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(file, JsonSerializer.Serialize(moved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Notes.Add($"Updating {file}: {ex.Message}"); }
    }

    static void MoveAutostart()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue(OldName) == null) return;
            run.DeleteValue(OldName, false);
            Autostart.Set(true);
            Notes.Add("Moved the start-with-Windows entry to the new name");
        }
        catch (Exception ex) { Notes.Add($"Autostart migration: {ex.Message}"); }
    }
}
