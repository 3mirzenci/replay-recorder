using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplayRecorder.Core;

public enum CaptureMethod { Wgc, Dda }
public enum VideoCodec { H264, Hevc }
public enum EncoderBackend { Auto, Nvenc, Qsv, X264 }

/// <summary>All user settings, stored as JSON in %APPDATA%\Replay Recorder\settings.json.</summary>
public sealed class Settings
{
    // --- Replay buffer ---
    public int BufferSeconds { get; set; } = 60;
    // Not Alt+F10/Alt+F9: the NVIDIA app overlay owns those for its own Instant Replay.
    public string SaveClipHotkey { get; set; } = "Alt+.";
    public string ToggleRecordingHotkey { get; set; } = "Ctrl+Alt+F9";
    /// <summary>
    /// Keep the replay buffer in memory instead of 2 s files on disk: no constant SSD writes,
    /// at the cost of roughly (buffer length x bitrate) of RAM, ~200-400 MB for 60 s at 1440p.
    /// </summary>
    public bool BufferInRam { get; set; } = false;

    // --- Video ---
    /// <summary>Monitor device name like \\.\DISPLAY1, or empty for the primary monitor.</summary>
    public string Monitor { get; set; } = "";
    public CaptureMethod Capture { get; set; } = CaptureMethod.Wgc;
    /// <summary>Output height: 0 = native (capped at MaxNativeHeight), otherwise 1080, 720, ...</summary>
    public int Height { get; set; } = 0;
    public int MaxNativeHeight { get; set; } = 1440;
    public int Fps { get; set; } = 60;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public EncoderBackend Encoder { get; set; } = EncoderBackend.Auto;
    /// <summary>NVENC constant-quality value (lower = better/larger). 19 high, 23 balanced, 27 small.</summary>
    public int Quality { get; set; } = 21;
    public int MaxBitrateMbps { get; set; } = 50;
    public bool CaptureCursor { get; set; } = true;

    // --- Audio ---
    public bool SystemAudioEnabled { get; set; } = true;
    /// <summary>
    /// WASAPI endpoint id to loopback-record, or empty for automatic: the Windows default output,
    /// except when that is a virtual device (HyperX NGENUITY, SteelSeries Sonar, VB-Cable...),
    /// whose loopback is silent; then the real headset/speakers behind it are used.
    /// </summary>
    public string SystemAudioDeviceId { get; set; } = "";
    public double SystemVolumeDb { get; set; } = 0;
    public bool MicEnabled { get; set; } = true;
    /// <summary>WASAPI endpoint id, or empty for the Windows default communications microphone.</summary>
    public string MicDeviceId { get; set; } = "";
    public double MicVolumeDb { get; set; } = 0;

    // --- Folders ---
    public string ClipsFolder { get; set; } = Paths.DefaultClipsFolder;
    public string BufferFolder { get; set; } = Paths.DefaultBufferFolder;
    public string ExportFolder { get; set; } = Paths.DefaultExportFolder;
    /// <summary>Warn when the clips folder grows past this many GB (0 = never warn).</summary>
    public double ClipsFolderLimitGB { get; set; } = 50;

    // --- Discord export ---
    /// <summary>Exports are guaranteed to be at most this many MB (1 MB = 1,000,000 bytes, so it is
    /// safe whichever way Discord counts its 20 MB limit).</summary>
    public double DiscordTargetMB { get; set; } = 19.5;

    // --- Misc ---
    public bool StartWithWindows { get; set; } = false;
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Short chime when a clip is saved (the popup can't show over exclusive-fullscreen games).</summary>
    public bool PlaySaveSound { get; set; } = true;
    public Dictionary<string, string> GameNames { get; set; } = GameDetector.DefaultNames();

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile), Json);
                if (s != null) return s.Normalized();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings file unreadable, using defaults: {ex.Message}");
        }
        var d = new Settings();
        d.Save();
        return d;
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.AppData);
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, Paths.SettingsFile, overwrite: true);
    }

    Settings Normalized()
    {
        BufferSeconds = Math.Clamp(BufferSeconds, 15, 300);
        Fps = Fps >= 45 ? 60 : 30;
        Quality = Math.Clamp(Quality, 10, 40);
        MaxBitrateMbps = Math.Clamp(MaxBitrateMbps, 5, 150);
        SystemVolumeDb = Math.Clamp(SystemVolumeDb, -30, 20);
        MicVolumeDb = Math.Clamp(MicVolumeDb, -30, 20);
        DiscordTargetMB = Math.Clamp(DiscordTargetMB, 1, 500);
        if (string.IsNullOrWhiteSpace(ClipsFolder)) ClipsFolder = Paths.DefaultClipsFolder;
        if (string.IsNullOrWhiteSpace(BufferFolder)) BufferFolder = Paths.DefaultBufferFolder;
        if (string.IsNullOrWhiteSpace(ExportFolder)) ExportFolder = Paths.DefaultExportFolder;
        GameNames = new Dictionary<string, string>(GameNames ?? GameDetector.DefaultNames(), StringComparer.OrdinalIgnoreCase);
        return this;
    }
}
