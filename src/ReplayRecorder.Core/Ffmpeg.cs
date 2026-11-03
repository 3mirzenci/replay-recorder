using System.Diagnostics;

namespace ReplayRecorder.Core;

/// <summary>Finds ffmpeg/ffprobe: next to the app first (bundled), then on PATH.</summary>
public static class Ffmpeg
{
    // winget's Links folder is on PATH only for processes started after ffmpeg was installed;
    // check it directly so a freshly installed ffmpeg works without logging out.
    static readonly string WingetLinks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");

    public static string? FfmpegPath { get; } = Find("ffmpeg.exe", "ffmpeg", WingetLinks);
    public static string? FfprobePath { get; } = Find("ffprobe.exe", "ffmpeg", WingetLinks);
    public static string? MpvPath { get; } = Find("mpv.exe", "mpv",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MPV Player"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mpv"));

    public const string InstallHint = "ffmpeg was not found. Install it with:  winget install Gyan.FFmpeg  (then restart Replay Recorder)";
    public const string MpvInstallHint = "The mpv player was not found. Install it with:  winget install shinchiro.mpv  (then reopen Replay Recorder)";

    static string? Find(string exe, string bundledDir = "ffmpeg", params string[] extraDirs)
    {
        var local = Path.Combine(AppContext.BaseDirectory, bundledDir, exe);
        if (File.Exists(local)) return local;
        local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;
        foreach (var d in extraDirs)
        {
            var p = Path.Combine(d, exe);
            if (File.Exists(p)) return p;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Runs ffmpeg/ffprobe to completion and returns (exit code, stderr).</summary>
    public static async Task<(int Code, string StdErr)> RunAsync(string exe, IEnumerable<string> args,
        Stream? stdin = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        ChildProcesses.Track(p);
        var err = p.StandardError.ReadToEndAsync(ct);
        if (stdin != null)
        {
            try { await stdin.CopyToAsync(p.StandardInput.BaseStream, ct); }
            catch (IOException) { /* ffmpeg closed stdin early; its exit code tells the story */ }
            p.StandardInput.Close();
        }
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await err);
    }
}
