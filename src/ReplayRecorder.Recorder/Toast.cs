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
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    int _ticks;

    Toast(string title, string detail, Color accent)
    {
        _title = title; _detail = detail; _accent = accent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 28);
        DoubleBuffered = true;
        Size = new Size(300, 64);
        Opacity = 0;
        _timer.Tick += (_, _) => Animate();
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
    public static void Show(string title, string detail, Color accent, Rectangle screenBounds)
    {
        _current?.Close();
        var t = new Toast(title, detail, accent);
        float scale = t.DeviceDpi / 96f;
        t.Size = new Size((int)(300 * scale), (int)(64 * scale));
        t.Location = new Point(screenBounds.Right - t.Width - (int)(24 * scale), screenBounds.Top + (int)(24 * scale));
        t.Region = new Region(RoundedRect(new Rectangle(Point.Empty, t.Size), (int)(10 * scale)));
        _current = t;
        t.Show();
        t._timer.Start();
    }

    void Animate()
    {
        _ticks++;
        // fade in 150 ms, hold ~2 s, fade out 300 ms
        if (_ticks <= 5) Opacity = _ticks / 5.0 * 0.95;
        else if (_ticks > 72) Opacity = Math.Max(0, 0.95 - (_ticks - 72) / 10.0);
        if (_ticks > 82) { _timer.Stop(); Close(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = DeviceDpi / 96f;
        using (var b = new SolidBrush(_accent)) g.FillEllipse(b, 16 * s, Height / 2f - 6 * s, 12 * s, 12 * s);
        using var f1 = new Font("Segoe UI Semibold", 11f);
        using var f2 = new Font("Segoe UI", 9f);
        TextRenderer.DrawText(g, _title, f1, new Point((int)(40 * s), (int)(12 * s)), Color.White);
        TextRenderer.DrawText(g, _detail, f2, new Rectangle((int)(40 * s), (int)(34 * s), Width - (int)(50 * s), (int)(20 * s)),
            Color.FromArgb(170, 170, 180), TextFormatFlags.EndEllipsis);
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
