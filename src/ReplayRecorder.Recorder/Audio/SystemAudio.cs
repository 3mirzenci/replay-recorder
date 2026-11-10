using System.Runtime.InteropServices;
using ReplayRecorder.Core;

namespace ReplayRecorder.Recorder.Audio;

/// <summary>
/// What you hear, split in two when Discord runs: Discord's process tree into the Discord sink and
/// everything else into the game sink (process loopback, include/exclude). Without Discord, with the
/// switch off, or when process loopback fails, the game sink gets today's device loopback (Discord
/// included) and the Discord track stays silent. Re-checks every few seconds, so Discord starting
/// late, restarting or updating is picked up.
/// </summary>
sealed class SystemAudio : IDisposable
{
    static readonly string[] DiscordNames = { "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe" };

    readonly string _deviceId;
    readonly bool _separate;
    readonly ClockedAudioSink _game, _discord;
    readonly object _gate = new();
    WasapiSource? _device;
    ProcessLoopback? _exclude, _include;
    int _pid = -1, _failedPid;
    System.Threading.Timer? _timer;
    bool _disposed;

    public SystemAudio(string deviceId, bool separateDiscord, ClockedAudioSink game, ClockedAudioSink discord)
    {
        _deviceId = deviceId;
        _separate = separateDiscord;
        _game = game;
        _discord = discord;
    }

    public void Start()
    {
        if (_separate && !ProcessLoopback.Supported)
            Log.Warn("Separate Discord track needs Windows 10 2004 or newer; Discord stays in the game track");
        if (!_separate || !ProcessLoopback.Supported) { Use(0); return; }
        // MTA: COM objects made on the STA UI thread can't be used from ProcessLoopback's capture thread (E_NOINTERFACE)
        Task.Run(Check).Wait();
        _timer = new System.Threading.Timer(_ => Check(), null, 3000, 3000);
    }

    void Check()
    {
        try
        {
            int pid = FindDiscord();
            if (pid == _failedPid) pid = 0; // refused once for this Discord: device loopback until it restarts
            lock (_gate) if (pid != _pid) Use(pid); // timer ticks can overlap a slow activation
        }
        catch (Exception ex) { Log.Warn($"Discord check: {ex.Message}"); }
    }

    void Use(int pid)
    {
        lock (_gate)
        {
            if (_disposed) return;
            CloseAll();
            _pid = pid;
            if (pid != 0)
            {
                try
                {
                    _exclude = new ProcessLoopback(pid, include: false, _game);
                    _include = new ProcessLoopback(pid, include: true, _discord);
                    Log.Info($"Discord (pid {pid}) on its own track; game track is everything else");
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Process loopback failed ({ex.Message}); Discord stays in the game track");
                    _failedPid = pid;
                    CloseAll();
                }
            }
            _device = new WasapiSource(loopback: true, _deviceId, _game);
            _device.Start();
        }
    }

    void CloseAll()
    {
        _exclude?.Dispose(); _exclude = null;
        _include?.Dispose(); _include = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate) { _disposed = true; CloseAll(); }
    }

    // ------------------------------------------------------------------ finding Discord

    /// <summary>Root Discord process (its parent isn't Discord), or 0.</summary>
    static int FindDiscord()
    {
        var names = new Dictionary<int, string>();
        var parents = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
        try
        {
            var e = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
            {
                names[e.Pid] = e.ExeFile;
                parents[e.Pid] = e.ParentPid;
            }
        }
        finally { CloseHandle(snap); }
        bool IsDiscord(int pid) => names.TryGetValue(pid, out var n) && DiscordNames.Contains(n, StringComparer.OrdinalIgnoreCase);
        return names.Keys.Where(IsDiscord).FirstOrDefault(p => !IsDiscord(parents[p]));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public int Size, Usage, Pid;
        public IntPtr DefaultHeapId;
        public int ModuleId, Threads, ParentPid, PriClassBase, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
