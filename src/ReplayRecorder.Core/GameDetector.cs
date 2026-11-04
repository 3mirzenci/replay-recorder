using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayRecorder.Core;

/// <summary>
/// Names a clip after the foreground app. Only reads the window title and the process
/// image path through public Win32 APIs; never opens the game for memory access.
/// </summary>
public static class GameDetector
{
    public static Dictionary<string, string> DefaultNames() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2.exe"] = "CS2",
        ["VALORANT-Win64-Shipping.exe"] = "Valorant",
        ["NBA2K26.exe"] = "NBA 2K26",
        ["RocketLeague.exe"] = "Rocket League",
        ["FortniteClient-Win64-Shipping.exe"] = "Fortnite",
        ["r5apex.exe"] = "Apex Legends",
        ["r5apex_dx12.exe"] = "Apex Legends",
        ["GTA5.exe"] = "GTA V",
        ["GTA5_Enhanced.exe"] = "GTA V",
        ["eldenring.exe"] = "Elden Ring",
        ["Cyberpunk2077.exe"] = "Cyberpunk 2077",
        ["RDR2.exe"] = "Red Dead Redemption 2",
        ["League of Legends.exe"] = "League of Legends",
        ["Minecraft.Windows.exe"] = "Minecraft",
        ["javaw.exe"] = "Minecraft",
    };

    /// <summary>Processes that mean "no game in front": the desktop, browsers, Replay Recorder itself.</summary>
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
        "ReplayRecorder.Recorder.exe", "ReplayRecorder.exe", "ApplicationFrameHost.exe", "LockApp.exe",
    };

    public static string Detect(IReadOnlyDictionary<string, string> names)
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Desktop";
            GetWindowThreadProcessId(hwnd, out var pid);
            var path = ProcessPath(pid);
            var exe = path == null ? null : Path.GetFileName(path);
            var title = WindowTitle(hwnd);

            if (exe != null && names.TryGetValue(exe, out var mapped)) return mapped;
            if (exe == null || NotGames.Contains(exe)) return "Desktop";

            // Unknown app: prefer the exe's product name, then the window title, then the exe name.
            var product = TryProductName(path!);
            var name = !string.IsNullOrWhiteSpace(product) ? product
                     : !string.IsNullOrWhiteSpace(title) ? title
                     : Path.GetFileNameWithoutExtension(exe);
            return Paths.SafeName(name!);
        }
        catch (Exception ex)
        {
            Log.Warn($"Game detection failed: {ex.Message}");
            return "Unknown";
        }
    }

    static string? TryProductName(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            var p = v.ProductName?.Trim();
            // Engines often stamp generic product names; skip those.
            if (string.IsNullOrEmpty(p) || p.Contains("Unreal Engine", StringComparison.OrdinalIgnoreCase)
                || p.Contains("Unity", StringComparison.OrdinalIgnoreCase) || p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                return null;
            return p;
        }
        catch { return null; }
    }

    static string? ProcessPath(uint pid)
    {
        // PROCESS_QUERY_LIMITED_INFORMATION is the least-privileged access right: name/path only.
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
}
