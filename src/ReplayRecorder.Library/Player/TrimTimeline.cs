using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library.Player;

/// <summary>
/// Timeline with a playhead and two trim handles. Click or drag on the tracks to seek; drag the
/// handles to set in/out, or drag inside a trimmed range to slide it.
///
/// Layout (docs/aurora/mockup.html): track names on the left, one lane per audio track drawn as thin
/// vertical stripes that follow the loudness, the kept range framed in the accent with a grip on each
/// side; the right <see cref="SliderColumn"/> px are left free for the volume sliders, which the
/// window lines up with the lanes (<see cref="LaneTop"/>, <see cref="LaneHeight"/>, <see cref="LaneGap"/>).
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
    /// Lanes: 1 = a single mixed track ("Audio"), 2 = game (+ chat) and mic, 3 = game, mic and Discord.
    /// Set from the clip's track layout when it opens, so the sliders beside the lanes line up before the levels load.
    /// </summary>
    public int Lanes
    {
        get => _lanes;
        set { _lanes = Math.Clamp(value, 1, 3); Height = LaneTop + LanesHeight + 26; InvalidateVisual(); }
    }

    /// <summary>
    /// Loudness per lane, in dBFS per 1/<paramref name="perSecond"/> s: game (+ chat), mic, Discord; null clears them.
    /// </summary>
    public void SetLevels(float[]? game, float[]? mic, float[]? discord, int perSecond)
    {
        _game = game; _mic = mic; _discord = discord; _levelsPerSecond = perSecond;
        // Each lane is scaled to its own clip (its loudest moment fills it), but only sound at least FullDb loud
        // can fill it: keyboard clicks on a mic (around -45 dB) stay small instead of looking like talk (-10 dB).
        _gameTop = game is { Length: > 0 } ? Math.Max(game.Max(), FullDb) : 0;
        _micTop = mic is { Length: > 0 } ? Math.Max(mic.Max(), FullDb) : 0;
        _discordTop = discord is { Length: > 0 } ? Math.Max(discord.Max(), FullDb) : 0;
        InvalidateVisual();
    }

    /// <summary>The mix sliders' gains (dB), so the stripes show what you'll hear.</summary>
    public void SetGains(double gameDb, double micDb, double discordDb)
    {
        _gameGain = gameDb; _micGain = micDb; _discordGain = discordDb;
        InvalidateVisual();
    }

    float[]? _game, _mic, _discord;
    int _levelsPerSecond = 10, _lanes = 2;
    double _gameGain, _micGain, _discordGain;
    double _gameTop, _micTop, _discordTop; // loudest level of each track (dBFS)
    const double RangeDb = 30; // a stripe is empty this far below its track's loudest moment
    const double FullDb = -20; // quietest level that can still fill a lane

    /// <summary>User asked to seek (seconds).</summary>
    public event Action<double>? SeekRequested;
    /// <summary>Trim range is changing during a drag (in, out).</summary>
    public event Action<double, double>? TrimChanged;
    /// <summary>A trim drag finished (in, out): handles or the whole range.</summary>
    public event Action<double, double>? TrimCommitted;

    public const double LaneTop = 8, LaneHeight = 26, LaneGap = 6;
    /// <summary>Room at the right for the volume sliders and their dB values.</summary>
    public const double SliderColumn = 170;
    const double MinLength = 0.5;
    const double MoveThreshold = 4; // px before a press inside the range becomes a move
    double LanesHeight => _lanes * LaneHeight + (_lanes - 1) * LaneGap;
    double _duration, _position, _in, _out;
    // PressRange: pressed inside a trimmed range; becomes Move once dragged, or a seek click on release.
    enum Drag { None, Seek, In, Out, PressRange, Move }
    Drag _drag;
    double _pressX, _moveFromIn, _moveLength;

    bool IsTrimmed => _in > 0.01 || _out < _duration - 0.01;
    bool InsideRange(double x) => IsTrimmed && x > X(_in) + 8 && x < X(_out) - 8;

    // Theme colours, looked up on each render so a theme switch shows on the next frame.
    Color Res(string key) => ((SolidColorBrush)FindResource(key)).Color;
    static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public TrimTimeline()
    {
        Lanes = 2;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    string[] Labels => _lanes switch
    {
        1 => new[] { "Audio".T() },
        2 => new[] { "Game + chat".T(), "Mic".T() },
        _ => new[] { "Game".T(), "Mic".T(), "Discord" },
    };

    double _left = 64; // where the lanes start: right of the widest track name
    double Right => Math.Max(_left + 20, ActualWidth - (Lanes > 1 ? SliderColumn : 6)); // one track: no sliders, full width (6 px for the grip)
    double X(double t) => _left + (_duration > 0 ? t / _duration : 0) * (Right - _left);
    double T(double x) => _duration <= 0 ? 0 : Math.Clamp((x - _left) / (Right - _left), 0, 1) * _duration;

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var subtle = Frozen(Res("Subtle"));
        var names = Labels.Select(s => new FormattedText(s, Lang.Culture, FlowDirection.LeftToRight, Face, 11, subtle, dpi)).ToArray();
        _left = Math.Max(64, names.Max(n => n.Width) + 14);
        double right = Right, bottom = LaneTop + LanesHeight;
        var lane = Frozen(Res("LaneBg"));
        for (int i = 0; i < _lanes; i++)
        {
            double y = LaneTop + i * (LaneHeight + LaneGap);
            dc.DrawText(names[i], new Point(0, y + (LaneHeight - names[i].Height) / 2));
            dc.DrawRoundedRectangle(lane, null, new Rect(_left, y, right - _left, LaneHeight), 6, 6);
        }
        if (_duration <= 0) return;

        var accent = Res("Accent");
        var colours = new[] { _lanes == 1 ? accent : Res("GameTrack"), accent, Res("DiscordTrack") };
        var levels = new[] { _game, _mic, _discord };
        var shifts = new[] { _gameGain + RangeDb - _gameTop, _micGain + RangeDb - _micTop, _discordGain + RangeDb - _discordTop };
        for (int i = 0; i < _lanes; i++)
            DrawStripes(dc, LaneTop + i * (LaneHeight + LaneGap), levels[i], shifts[i], colours[i]);

        // The kept range: a rounded accent frame round all lanes, a grip on each side.
        double xi = X(_in), xo = X(_out);
        var accentBrush = Frozen(accent);
        dc.DrawRoundedRectangle(null, FrozenPen(accent, 2), new Rect(xi, LaneTop - 4, Math.Max(0, xo - xi), LanesHeight + 8), 8, 8);
        double gripH = Math.Max(18, LanesHeight * 0.5), gripY = LaneTop + (LanesHeight - gripH) / 2;
        var onAccent = Frozen(Res("OnAccent"));
        foreach (var x in new[] { xi, xo })
        {
            dc.DrawRoundedRectangle(accentBrush, null, new Rect(x - 5, gripY, 10, gripH), 5, 5);
            dc.DrawRectangle(onAccent, null, new Rect(x - 1, gripY + gripH / 2 - 7, 2, 14));
        }

        // Time labels under the lanes.
        var faint = Frozen(Res("Faint"));
        double step = NiceStep(_duration, right - _left);
        for (double t = 0; t <= _duration + 0.001; t += step)
        {
            var ft = new FormattedText(Fmt(t), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, faint, dpi);
            double x = X(t);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, _left - 4, right - ft.Width), bottom + 8));
        }

        // Playhead: a white line with a dot on top.
        double xp = X(Math.Clamp(_position, 0, _duration));
        dc.DrawRectangle(Brushes.White, null, new Rect(xp - 1, LaneTop - 8, 2, LanesHeight + 14));
        dc.DrawEllipse(Brushes.White, null, new Point(xp, LaneTop - 8), 5, 5);
    }

    /// <summary>
    /// One lane: a 2 px stripe every 4 px, as tall as the loudest moment under it, centred; bright in the kept
    /// range, faded outside it. <paramref name="shift"/> (dB) maps the track's loudest moment, at slider 0, to full height.
    /// </summary>
    void DrawStripes(DrawingContext dc, double top, float[]? lv, double shift, Color colour)
    {
        if (lv == null) return;
        const double Step = 4, W = 2, Edge = 2;
        var kept = Frozen(Color.FromArgb(0xF2, colour.R, colour.G, colour.B));
        var cut = Frozen(Color.FromArgb(0x38, colour.R, colour.G, colour.B));
        double mid = top + LaneHeight / 2, maxH = LaneHeight - 2 * Edge, xi = X(_in), xo = X(_out);
        for (double x = _left + 1; x + W <= Right - 2; x += Step)
        {
            int a = (int)(T(x) * _levelsPerSecond), b = Math.Max(a + 1, (int)Math.Ceiling(T(x + Step) * _levelsPerSecond));
            float max = float.MinValue;
            for (int i = Math.Max(0, a); i < Math.Min(lv.Length, b); i++) max = Math.Max(max, lv[i]);
            if (max == float.MinValue) continue;
            double h = Math.Clamp((max + shift) / RangeDb, 0, 1) * maxH;
            if (h >= 1) dc.DrawRoundedRectangle(x >= xi && x <= xo ? kept : cut, null, new Rect(x, mid - h / 2, W, h), 1, 1);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        var x = e.GetPosition(this).X;
        if (x < _left - 10 || x > Right + 10) return; // the names and the slider column aren't timeline
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
            Cursor = x < _left - 10 || x > Right + 10 ? Cursors.Arrow
                   : Math.Abs(x - X(_in)) <= 8 || Math.Abs(x - X(_out)) <= 8 ? Cursors.SizeWE
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
