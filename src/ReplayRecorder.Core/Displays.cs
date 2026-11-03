using System.Runtime.InteropServices;

namespace ReplayRecorder.Core;

public sealed record DisplayInfo(
    IntPtr HMonitor,
    string DeviceName,     // \\.\DISPLAY1
    string AdapterName,    // NVIDIA GeForce RTX 4060 Laptop GPU
    int Width, int Height, // physical pixels (process is per-monitor DPI aware)
    int RefreshHz,
    bool Primary)
{
    /// <summary>Monitor model from its EDID, e.g. "LG ULTRAGEAR" (empty if unknown).</summary>
    public string MonitorName { get; init; } = "";
    /// <summary>Windows HDR is switched on for this display.</summary>
    public bool HdrEnabled { get; init; }

    public bool OnNvidia => AdapterName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    public bool OnIntel => AdapterName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
    public string Label => $"{(MonitorName.Length > 0 ? MonitorName : DeviceName.TrimStart('\\', '.'))} · {Width}×{Height} @ {RefreshHz} Hz{(Primary ? " · primary" : "")}";
    public override string ToString() =>
        $"{DeviceName} \"{MonitorName}\" {Width}x{Height}@{RefreshHz}Hz on {AdapterName}{(Primary ? " (primary)" : "")}{(HdrEnabled ? " HDR" : "")}";
}

public static class Displays
{
    public static List<DisplayInfo> Enumerate()
    {
        var targets = QueryTargets();
        var list = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref mi)) return true;

            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            EnumDisplaySettings(mi.szDevice, -1 /* ENUM_CURRENT_SETTINGS */, ref dm);

            targets.TryGetValue(mi.szDevice, out var t);
            list.Add(new DisplayInfo(h, mi.szDevice, AdapterFor(mi.szDevice),
                mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                dm.dmDisplayFrequency, (mi.dwFlags & 1) != 0)
            { MonitorName = t.Name ?? "", HdrEnabled = t.Hdr });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Monitor names and HDR state via the DisplayConfig API, keyed by GDI device name
    /// (\\.\DISPLAY1). Returns an empty map if the API is unavailable.
    /// </summary>
    static Dictionary<string, (string? Name, bool Hdr)> QueryTargets()
    {
        var map = new Dictionary<string, (string?, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return map;
            var paths = new DISPLAYCONFIG_PATH_INFO[np];
            var modes = new DISPLAYCONFIG_MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return map;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                src.header = Header(1, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), p.sourceAdapterLow, p.sourceAdapterHigh, p.sourceId);
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tgt.header = Header(2, Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), p.targetAdapterLow, p.targetAdapterHigh, p.targetId);
                string? name = DisplayConfigGetDeviceInfo(ref tgt) == 0 ? tgt.monitorFriendlyDeviceName : null;

                var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                color.header = Header(9, Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(), p.targetAdapterLow, p.targetAdapterHigh, p.targetId);
                bool hdr = DisplayConfigGetDeviceInfo(ref color) == 0 && (color.value & 0x2) != 0; // advancedColorEnabled

                map[src.viewGdiDeviceName] = (name, hdr);
            }
        }
        catch (Exception ex) { Log.Warn($"DisplayConfig query failed: {ex.Message}"); }
        return map;
    }

    static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(int type, int size, uint low, int high, uint id) =>
        new() { type = type, size = size, adapterLow = low, adapterHigh = high, id = id };

    /// <summary>The monitor from settings, or the primary one.</summary>
    public static DisplayInfo? Pick(string deviceName)
    {
        var all = Enumerate();
        return all.FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.Primary) ?? all.FirstOrDefault();
    }

    public static void LogAll()
    {
        foreach (var d in Enumerate()) Log.Info($"Display: {d}");
    }

    static string AdapterFor(string device)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (string.Equals(dd.DeviceName, device, StringComparison.OrdinalIgnoreCase)) return dd.DeviceString;
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return "unknown adapter";
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    // --- DisplayConfig interop (monitor names, HDR) ---
    const uint QDC_ONLY_ACTIVE_PATHS = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO
    {
        public uint sourceAdapterLow; public int sourceAdapterHigh; public uint sourceId, sourceModeIdx, sourceStatus;
        public uint targetAdapterLow; public int targetAdapterHigh; public uint targetId, targetModeIdx;
        public uint outputTechnology, rotation, scaling, refreshNum, refreshDen, scanLineOrdering;
        public int targetAvailable; public uint targetStatus;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct DISPLAYCONFIG_MODE_INFO { public uint infoType, id, adapterLow; public int adapterHigh; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type, size; public uint adapterLow; public int adapterHigh; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value, colorEncoding, bitsPerColorChannel;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO info);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint i, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
}
