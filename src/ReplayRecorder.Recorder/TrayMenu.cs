using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ReplayRecorder.Recorder;

/// <summary>
/// The tray menu as a real Windows menu (TrackPopupMenuEx), in Windows' dark style with Segoe Fluent icons.
/// A WinForms ContextMenuStrip is a normal window, and the taskbar's hidden-icons flyout closes as soon as
/// the mouse moves onto one; real menus keep it open, like other apps' tray menus. The ToolStripMenuItems
/// stay the model (text, shortcut, checked, enabled, visible); the native menu is built from them on each open.
/// </summary>
static class TrayMenu
{
    /// <summary>Gives a menu item an icon: a Segoe Fluent Icons glyph.</summary>
    public static T WithIcon<T>(this T item, char glyph) where T : ToolStripItem { item.Tag = glyph; return item; }

    /// <summary>Dark native menus for this app (Windows 10 1903+; the app is always dark). Undocumented uxtheme
    /// calls that Notepad++ and many others use; harmless where missing.</summary>
    public static void UseDarkMenus()
    {
        try { SetPreferredAppMode(2 /* ForceDark */); FlushMenuThemes(); } catch { }
    }

    static NativeWindow? _owner;

    public static void Show(ToolStripItemCollection items)
    {
        if (_owner == null) { _owner = new NativeWindow(); _owner.CreateHandle(new CreateParams()); }
        var hmenu = CreatePopupMenu();
        var commands = new Dictionary<int, ToolStripMenuItem>();
        var bitmaps = new List<IntPtr>();
        try
        {
            bool lastWasSeparator = true;
            foreach (ToolStripItem item in items)
            {
                if (!item.Available) continue;
                if (item is ToolStripSeparator)
                {
                    if (!lastWasSeparator) AppendMenu(hmenu, MF_SEPARATOR, UIntPtr.Zero, null);
                    lastWasSeparator = true;
                    continue;
                }
                if (item is not ToolStripMenuItem mi) continue;
                int id = commands.Count + 1;
                commands[id] = mi;
                var text = mi.Text?.Replace("&", "&&") ?? "";
                if (!string.IsNullOrEmpty(mi.ShortcutKeyDisplayString)) text += "\t" + mi.ShortcutKeyDisplayString;
                else if (!mi.Enabled) { int dot = text.IndexOf(" · "); if (dot > 0) text = text[..dot] + "\t" + text[(dot + 3)..]; } // status line: "buffer 60s" goes in the shortcut column, so it doesn't widen the menu
                uint flags = MF_STRING | (mi.Enabled ? 0 : MF_GRAYED) | (mi.Checked ? MF_CHECKED : 0);
                AppendMenu(hmenu, flags, (UIntPtr)id, text);
                if (mi.Tag is char glyph)
                {
                    var bmp = GlyphBitmap(glyph);
                    bitmaps.Add(bmp);
                    var info = new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_BITMAP, hbmpItem = bmp };
                    SetMenuItemInfo(hmenu, (uint)id, false, ref info);
                }
                if (mi.Font.Bold) SetMenuDefaultItem(hmenu, (uint)id, 0); // bold, like "Install update"
                lastWasSeparator = false;
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(_owner.Handle); // required, or the menu doesn't close when clicking elsewhere
            int cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, pt.X, pt.Y, _owner.Handle, IntPtr.Zero);
            PostMessage(_owner.Handle, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            if (commands.TryGetValue(cmd, out var chosen)) chosen.PerformClick();
        }
        finally
        {
            DestroyMenu(hmenu);
            foreach (var b in bitmaps) DeleteObject(b);
        }
    }

    static readonly string IconFontName = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>A white glyph on transparent, as the premultiplied 32-bit bitmap native menus draw with alpha.</summary>
    internal static IntPtr GlyphBitmap(char glyph)
    {
        using var probe = Graphics.FromHwnd(IntPtr.Zero);
        int size = (int)Math.Round(16 * probe.DpiY / 96f);
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var path = new GraphicsPath())
        using (var family = new FontFamily(IconFontName))
        {
            // The glyph's own outline, scaled to fit and centred: icon fonts sit oddly in their character box.
            path.AddString(glyph.ToString(), family, 0, size, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            float scale = size * 0.875f / Math.Max(b.Width, b.Height);
            using var m = new Matrix();
            m.Translate(size / 2f, size / 2f);
            m.Scale(scale, scale);
            m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
            path.Transform(m);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(Brushes.White, path);
        }

        var bi = new BITMAPINFO { biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        var hbmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
        var data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * size * 4, size * 4);
        bmp.UnlockBits(data);
        for (int i = 0; i < px.Length; i += 4) // premultiply
        {
            int a = px[i + 3];
            px[i] = (byte)(px[i] * a / 255); px[i + 1] = (byte)(px[i + 1] * a / 255); px[i + 2] = (byte)(px[i + 2] * a / 255);
        }
        Marshal.Copy(px, 0, bits, px.Length);
        return hbmp;
    }

    const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x800, MIIM_BITMAP = 0x80;
    const uint TPM_RIGHTBUTTON = 2, TPM_RIGHTALIGN = 8, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant, bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetMenuItemInfo(IntPtr hMenu, uint item, bool byPosition, ref MENUITEMINFO info);
    [DllImport("user32.dll")] static extern bool SetMenuDefaultItem(IntPtr hMenu, uint item, uint byPos);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
}
