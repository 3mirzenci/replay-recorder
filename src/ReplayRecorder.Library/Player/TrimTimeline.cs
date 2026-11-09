using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ReplayRecorder.Library.Player;

/// <summary>
/// Timeline with a playhead and two trim handles. Click or drag on the bar to seek; drag the
/// handles to set in/out. Everything outside the trim range is dimmed.
/// </summary>
sealed class TrimTimeline : FrameworkElement
{
    public double Duration { get => _duration; set { _duration = Math.Max(0, value); InvalidateVisual(); } }
    public double Position { get => _position; set { _position = value; InvalidateVisual(); } }
    public double In { get => _in; set { _in = Math.Clamp(value, 0, Math.Max(0, Out - MinLength)); InvalidateVisual(); } }
    public double Out { get => _out; set { _out = Math.Clamp(value, Math.Min(_duration, In + MinLength), _duration); InvalidateVisual(); } }

    /// <summary>Sets duration and trim range together (no ordering surprises from the clamping setters).</summary>
    public void SetClip(double duration, double inPoint, double outPoint)
    {
        _duration = Math.Max(0, duration);
        _out = outPoint > 0 ? Math.Clamp(outPoint, 0, _duration) : _duration;
        _in = Math.Clamp(inPoint, 0, Math.Max(0, _out - MinLength));
        InvalidateVisual();
    }

    /// <summary>
    /// Sound bars: game (+ chat) loudness in the top bar, mic in the bottom one; a single-track
    /// clip gets one bar. dBFS per 1/<paramref name="perSecond"/> s; null clears them.
    /// </summary>
    public void SetLevels(float[]? game, float[]? mic, int perSecond)
    {
        _game = game; _mic = mic; _levelsPerSecond = perSecond;
        InvalidateVisual();
    }

    /// <summary>The mix sliders' gains (dB), so the bars show what you'll hear.</summary>
    public void SetGains(double gameDb, double micDb)
    {
        _gameGain = gameDb; _micGain = micDb;
        InvalidateVisual();
    }

    float[]? _game, _mic;
    int _levelsPerSecond = 10;
    double _gameGain, _micGain;
    // Bar scale in dBFS RMS: game sits around -30 to -10 in loud games, talking around -40 to -30.
    const double QuietDb = -50, LoudDb = -10;

    /// <summary>User asked to seek (seconds).</summary>
    public event Action<double>? SeekRequested;
    /// <summary>Trim range is changing during a drag (in, out).</summary>
    public event Action<double, double>? TrimChanged;
    /// <summary>A trim drag finished (in, out): handles or the whole range.</summary>
    public event Action<double, double>? TrimCommitted;

    const double MinLength = 0.5;
    const double Pad = 10;      // horizontal padding so handles at the ends stay grabbable
    const double BarH = 48; // both sound bars
    const double StripGap = 4;
    const double MoveThreshold = 4; // px before a press inside the range becomes a move
    double _duration, _position, _in, _out;
    // PressRange: pressed inside a trimmed range; becomes Move once dragged, or a seek click on release.
    enum Drag { None, Seek, In, Out, PressRange, Move }
    Drag _drag;
    double _pressX, _moveFromIn, _moveLength;

    bool IsTrimmed => _in > 0.01 || _out < _duration - 0.01;
    bool InsideRange(double x) => IsTrimmed && x > X(_in) + 8 && x < X(_out) - 8;

    static readonly Brush Head = Frozen(Colors.White);
    static readonly Brush Tick = Frozen(Color.FromRgb(0x6b, 0x6f, 0x7a));
    // Theme colours, looked up on each render so a theme switch shows on the next frame.
    Color Res(string key) => ((SolidColorBrush)FindResource(key)).Color;
    static readonly Typeface Face = new("Segoe UI");

    public TrimTimeline()
    {
        Height = BarH + 22;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    double X(double t) => Pad + (_duration > 0 ? t / _duration : 0) * (ActualWidth - 2 * Pad);
    double T(double x) => _duration <= 0 ? 0 : Math.Clamp((x - Pad) / (ActualWidth - 2 * Pad), 0, 1) * _duration;

    // Game (+ chat) and mic as two separate bars; one bar for a single-track clip.
    Rect[] Strips(double w)
    {
        double width = Math.Max(0, w - 2 * Pad);
        if (_game != null && _mic == null) return new[] { new Rect(Pad, 0, width, BarH) };
        double h = (BarH - StripGap) / 2;
        return new[] { new Rect(Pad, 0, width, h), new Rect(Pad, h + StripGap, width, h) };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, top = 0;
        var strips = Strips(w);
        var card = Frozen(Res("CardHover"));
        foreach (var s in strips) dc.DrawRoundedRectangle(card, null, s, 5, 5);
        if (_duration <= 0) return;

        var accent = Res("Accent");
        var sub = Res("Subtle");
        DrawLevels(dc, strips[0], _game, _gameGain, Frozen(Color.FromArgb(0xc0, sub.R, sub.G, sub.B)));
        if (strips.Length > 1) DrawLevels(dc, strips[1], _mic, _micGain, Frozen(Color.FromArgb(0xe0, accent.R, accent.G, accent.B)));

        var bg = Res("Bg");
        var Range = Frozen(Color.FromArgb(0x50, accent.R, accent.G, accent.B));
        var Dim = Frozen(Color.FromArgb(0xb0, bg.R, bg.G, bg.B));
        var handlePen = FrozenPen(accent, 3);
        double xi = X(_in), xo = X(_out);
        foreach (var s in strips)
        {
            dc.DrawRectangle(Range, null, new Rect(xi, s.Top, Math.Max(0, xo - xi), s.Height));
            dc.DrawRectangle(Dim, null, new Rect(Pad, s.Top, Math.Max(0, xi - Pad), s.Height));
            dc.DrawRectangle(Dim, null, new Rect(xo, s.Top, Math.Max(0, w - Pad - xo), s.Height));
        }

        // Second ticks + labels below the bars.
        double step = NiceStep(_duration, w);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double t = 0; t <= _duration + 0.001; t += step)
        {
            double x = X(t);
            dc.DrawRectangle(Tick, null, new Rect(x, BarH + 2, 1, 4));
            var ft = new FormattedText(Fmt(t), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Tick, dpi);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, 0, w - ft.Width), BarH + 6));
        }

        // Trim handles: brackets across both bars.
        DrawHandle(dc, handlePen, xi, true);
        DrawHandle(dc, handlePen, xo, false);

        // Playhead.
        double xp = X(Math.Clamp(_position, 0, _duration));
        dc.DrawRectangle(Head, null, new Rect(xp - 1, top - 3, 2, BarH + 6));
    }

    /// <summary>One sound bar: the loudest moment under each column, plus the slider gain, growing up from the bottom.</summary>
    void DrawLevels(DrawingContext dc, Rect strip, float[]? lv, double gain, Brush brush)
    {
        if (lv == null || strip.Width < 10) return;
        const double Edge = 3, Step = 3, Width = 2;
        double baseY = strip.Bottom - Edge, maxH = strip.Height - 2 * Edge;
        for (double x = strip.Left + 2; x + Width <= strip.Right - 2; x += Step)
        {
            int a = (int)(T(x) * _levelsPerSecond), b = Math.Max(a + 1, (int)Math.Ceiling(T(x + Step) * _levelsPerSecond));
            float max = float.MinValue;
            for (int i = Math.Max(0, a); i < Math.Min(lv.Length, b); i++) max = Math.Max(max, lv[i]);
            if (max == float.MinValue) continue;
            double h = Math.Clamp((max + gain - QuietDb) / (LoudDb - QuietDb), 0, 1) * maxH;
            if (h > 0.5) dc.DrawRectangle(brush, null, new Rect(x, baseY - h, Width, h));
        }
    }

    static void DrawHandle(DrawingContext dc, Pen pen, double x, bool left)
    {
        double d = left ? 1 : -1;
        dc.DrawLine(pen, new Point(x, 0), new Point(x, BarH));
        dc.DrawLine(pen, new Point(x, 1.5), new Point(x + 7 * d, 1.5));
        dc.DrawLine(pen, new Point(x, BarH - 1.5), new Point(x + 7 * d, BarH - 1.5));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        var x = e.GetPosition(this).X;
        _pressX = x;
        if (Math.Abs(x - X(_in)) <= 8) _drag = Drag.In;
        else if (Math.Abs(x - X(_out)) <= 8) _drag = Drag.Out;
        else if (InsideRange(x)) { _drag = Drag.PressRange; _moveFromIn = _in; _moveLength = _out - _in; }
        else { _drag = Drag.Seek; Position = T(x); SeekRequested?.Invoke(Position); }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        if (_drag == Drag.None)
        {
            Cursor = Math.Abs(x - X(_in)) <= 8 || Math.Abs(x - X(_out)) <= 8 ? Cursors.SizeWE
                   : InsideRange(x) ? Cursors.SizeAll : Cursors.Hand;
            return;
        }
        if (_drag == Drag.PressRange && Math.Abs(x - _pressX) >= MoveThreshold) _drag = Drag.Move;

        double t = T(x);
        switch (_drag)
        {
            case Drag.Seek: Position = t; SeekRequested?.Invoke(t); break;
            case Drag.In: In = t; Position = _in; SeekRequested?.Invoke(_in); TrimChanged?.Invoke(_in, _out); break;
            case Drag.Out: Out = t; Position = _out; SeekRequested?.Invoke(_out); TrimChanged?.Invoke(_in, _out); break;
            case Drag.Move:
                // Slide the whole range, keeping its length, clamped to the clip.
                double newIn = Math.Clamp(_moveFromIn + (T(x) - T(_pressX)), 0, _duration - _moveLength);
                _in = newIn; _out = newIn + _moveLength;
                Position = _in;
                InvalidateVisual();
                SeekRequested?.Invoke(_in);
                TrimChanged?.Invoke(_in, _out);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        switch (drag)
        {
            case Drag.PressRange: // a click inside the range without dragging: seek there
                Position = T(_pressX);
                SeekRequested?.Invoke(Position);
                break;
            case Drag.In or Drag.Out or Drag.Move:
                TrimCommitted?.Invoke(_in, _out);
                break;
        }
    }

    static double NiceStep(double duration, double width)
    {
        double maxTicks = Math.Max(2, width / 70);
        foreach (var s in new double[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
            if (duration / s <= maxTicks) return s;
        return 1200;
    }

    public static string Fmt(double t)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, t));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Pen FrozenPen(Color c, double w) { var p = new Pen(Frozen(c), w); p.Freeze(); return p; }
}
