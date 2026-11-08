using System.Diagnostics;

namespace ReplayRecorder.Core;

/// <summary>Finds ffmpeg/ffprobe: next to the app first (bundled), then on PATH.</summary>
public static class Ffmpeg
{
    // winget's Links folder is on PATH only for processes started after ffmpeg was installed;
    // check it directly so a freshly installed ffmpeg works without logging out.
    static readonly string WingetLinks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");

    // Without symlink rights (no Developer Mode) winget skips Links and puts the package's own bin folder
    // on the user PATH instead, which a process started by setup doesn't see yet.
    static readonly string[] WingetFfmpegBins = WingetPackageDirs("Gyan.FFmpeg");

    public static string? FfmpegPath { get; } = Find("ffmpeg.exe", "ffmpeg", [WingetLinks, .. WingetFfmpegBins]);
    public static string? FfprobePath { get; } = Find("ffprobe.exe", "ffmpeg", [WingetLinks, .. WingetFfmpegBins]);
    public static string? MpvPath { get; } = Find("mpv.exe", "mpv",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MPV Player"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mpv"));

    /// <summary>
    /// Our own small ffmpeg for recording (ffmpeg/build.sh): gfxcapture paced to the output rate, drawing NV12
    /// straight for NVENC. Shipped in capture\ next to the app; null when missing (then the normal ffmpeg is used).
    /// </summary>
    public static string? CapturePath { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg-capture.exe"))
            ? Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg-capture.exe") : null;

    public const string InstallHint = "ffmpeg is missing. Right-click Start, open Terminal and run:  winget install Gyan.FFmpeg  then restart Replay Recorder.";
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
        // This process's PATH, then the saved user and machine PATH: those already include anything
        // installed since this process (or the setup that started it) was launched.
        var path = string.Join(';',
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Windows Graphics Capture (gfxcapture) only exists in ffmpeg 8.0 and newer.</summary>
    public static bool HasGfxCapture => _hasGfxCapture.Value;
    static readonly Lazy<bool> _hasGfxCapture = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo(FfmpegPath!, "-hide_banner -filters") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            var list = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return list.Contains(" gfxcapture ");
        }
        catch { return true; } // can't tell: try it and let the normal error handling report failures
    });

    /// <summary>The bin folders of a winget portable package, e.g. Packages\Gyan.FFmpeg_…\ffmpeg-9.0-full_build\bin.</summary>
    static string[] WingetPackageDirs(string id)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (!Directory.Exists(root)) return [];
            return Directory.GetDirectories(root, id + "_*")
                .SelectMany(pkg => Directory.GetDirectories(pkg).Select(d => Path.Combine(d, "bin")).Append(pkg))
                .ToArray();
        }
        catch { return []; }
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
        // Saves and exports can wait; a game running alongside shouldn't lose frames to them.
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
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
