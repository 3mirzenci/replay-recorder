using System.IO;
using System.Runtime.InteropServices;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library;

/// <summary>Moves files to the Recycle Bin (never a permanent delete).</summary>
static class RecycleBin
{
    public static bool Send(string path)
    {
        // mpv may need a moment to release the file after "stop".
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };
            int r = SHFileOperation(ref op);
            if (r == 0 && !op.fAnyOperationsAborted && !File.Exists(path)) return true;
            Thread.Sleep(100);
        }
        Log.Warn($"Recycle Bin move failed for {path}");
        return false;
    }

    /// <summary>Moves several files in one operation (one Recycle Bin undo). Returns the paths that are still there.</summary>
    public static List<string> SendMany(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return new();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var left = paths.Where(File.Exists).ToList();
            if (left.Count == 0) return left;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", left) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };
            SHFileOperation(ref op);
            if (!left.Any(File.Exists)) return new();
            Thread.Sleep(100);
        }
        var failed = paths.Where(File.Exists).ToList();
        foreach (var f in failed) Log.Warn($"Recycle Bin move failed for {f}");
        return failed;
    }

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
