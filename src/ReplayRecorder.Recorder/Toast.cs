using System.Drawing.Drawing2D;

namespace ReplayRecorder.Recorder;

/// <summary>
/// Small "Clip saved" pop-up. It is a separate topmost, click-through window that never
/// activates, so it can't steal focus from a game (and is not drawn inside the game).
/// </summary>
sealed class Toast : Form
{
    const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
              WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

    static Toast? _current;
    readonly string _title, _detail;
    readonly Color _accent;
    readonly bool _aurora;
    // Aurora tokens (Themes/Aurora.xaml): Panel, Line (8% white on Panel), Text, Subtle
    static readonly Color AuroraPanel = Color.FromArgb(17, 17, 19), AuroraLine = Color.FromArgb(36, 36, 38),
                          AuroraText = Color.FromArgb(242, 242, 243), AuroraSubtle = Color.FromArgb(163, 163, 168);
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2300 };

    Toast(string title, string detail, Color accent, bool aurora)
    {
        _title = title; _detail = detail; _accent = accent; _aurora = aurora;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = aurora ? AuroraPanel : Color.FromArgb(24, 24, 28);
        DoubleBuffered = true;
        Size = new Size(300, 64);
        // Fixed opacity, no fade: changing a topmost window's alpha every frame over a borderless game
        // forces Windows to composite every game frame, which stutters the game while it animates.
        Opacity = 0.95;
        _timer.Tick += (_, _) => { _timer.Stop(); Close(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Shows a toast in the top-right corner of <paramref name="screenBounds"/>.</summary>
    public static void Show(string title, string detail, Color accent, Rectangle screenBounds, bool aurora)
    {
        _current?.Close();
        var t = new Toast(title, detail, accent, aurora);
        float scale = t.DeviceDpi / 96f;
        t.Size = new Size((int)(300 * scale), (int)(64 * scale));
        t.Location = new Point(screenBounds.Right - t.Width - (int)(24 * scale), screenBounds.Top + (int)(24 * scale));
        t.Region = new Region(RoundedRect(new Rectangle(Point.Empty, t.Size), (int)((aurora ? 14 : 10) * scale)));
        _current = t;
        t.Show();
        t._timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f;
        if (_aurora)
        {
            using var line = new Pen(AuroraLine, 1);
            using var edge = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), (int)(14 * s));
            g.DrawPath(line, edge);
        }
        using (var b = new SolidBrush(_accent)) g.FillEllipse(b, 16 * s, Height / 2f - 6 * s, 12 * s, 12 * s);
        using var f1 = new Font("Segoe UI Semibold", 11f);
        using var f2 = new Font("Segoe UI", 9f);
        TextRenderer.DrawText(g, _title, f1, new Point((int)(40 * s), (int)(12 * s)), _aurora ? AuroraText : Color.White);
        TextRenderer.DrawText(g, _detail, f2, new Rectangle((int)(40 * s), (int)(34 * s), Width - (int)(50 * s), (int)(20 * s)),
            _aurora ? AuroraSubtle : Color.FromArgb(170, 170, 180), TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        if (_current == this) _current = null;
        base.Dispose(disposing);
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
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
