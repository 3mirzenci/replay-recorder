using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ReplayRecorder.Core;

namespace ReplayRecorder.Recorder;

/// <summary>
/// Dark, rounded tray menu in the library's colours (Windows 11 style): icons from Segoe Fluent Icons,
/// a rounded hover highlight and the accent colour for checks. Replaces WinForms' grey default look.
/// </summary>
static class TrayMenu
{
    static readonly Color Bg = Color.FromArgb(0x20, 0x20, 0x24);
    static readonly Color Hover = Color.FromArgb(0x33, 0x33, 0x39);
    static readonly Color Fore = Color.FromArgb(0xF2, 0xF2, 0xF4);
    static readonly Color Dim = Color.FromArgb(0x8E, 0x8E, 0x98);
    static readonly Color Line = Color.FromArgb(0x3A, 0x3A, 0x42);

    /// <summary>The accent picked in Settings, else the theme's (Amber or Windows blue).</summary>
    public static Color Accent(Settings s)
    {
        try { if (s.AccentColor.Length > 0) return ColorTranslator.FromHtml(s.AccentColor); } catch { }
        return s.Theme is "amber" or "booth" ? Color.FromArgb(0xFF, 0xB6, 0x27) : Color.FromArgb(0x4C, 0xC2, 0xFF);
    }

    public static void Style(ContextMenuStrip menu, Color accent)
    {
        menu.Renderer = new Renderer(accent);
        menu.BackColor = Bg;
        menu.ForeColor = Fore;
        menu.Font = new Font("Segoe UI", 9.75f);
        menu.ShowImageMargin = true;
        menu.ImageScalingSize = new Size(20, 20); // width of the icon margin
        menu.Padding = new Padding(4, 6, 4, 6);
        foreach (ToolStripItem item in menu.Items)
            if (item is ToolStripMenuItem) item.Padding = new Padding(4, 5, 12, 5);
        // Windows 11 rounds menus it draws itself; ask the same for this one (ignored on Windows 10).
        menu.HandleCreated += (_, _) =>
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(menu.Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        };
    }

    public static void SetAccent(ContextMenuStrip menu, Color accent) => menu.Renderer = new Renderer(accent);

    /// <summary>Gives a menu item an icon: a Segoe Fluent Icons glyph the renderer draws beside its text.</summary>
    public static T WithIcon<T>(this T item, char glyph) where T : ToolStripItem { item.Tag = glyph; return item; }

    // Drawn as font text, not a bitmap: sharp at any display scaling, and centred on the text line.
    static readonly Font IconFont = new(FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 10.5f);

    static readonly StringFormat Centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    // Grey anti-aliasing, not ClearType: ClearType tints thin strokes (the pause bars) orange and blue.
    static void DrawGlyph(Graphics g, char glyph, Rectangle box, Color color)
    {
        var hint = g.TextRenderingHint;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color);
        g.DrawString(glyph.ToString(), IconFont, brush, box, Centered);
        g.TextRenderingHint = hint;
    }

    sealed class Renderer(Color accent) : ToolStripProfessionalRenderer(new Colors())
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Bg);
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { } // no grey strip behind icons

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(r, 5);
            using var brush = new SolidBrush(Hover);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Fore : Dim;
            base.OnRenderItemText(e);
            // The icon sits in the margin left of the text, on the same centre line (not for the shortcut text).
            if (e.Text != e.Item.Text) return; // the shortcut text
            // Icon, or the check mark for a ticked item without one: same box, so they all line up.
            var box = new Rectangle(4, e.TextRectangle.Y, e.TextRectangle.Left - 8, e.TextRectangle.Height);
            if (e.Item.Tag is char glyph) DrawGlyph(e.Graphics, glyph, box, e.TextColor);
            else if (e.Item is ToolStripMenuItem { Checked: true }) DrawGlyph(e.Graphics, '', box, accent); // CheckMark
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { } // drawn with the icons, in OnRenderItemText

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Fore;
            base.OnRenderArrow(e);
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
