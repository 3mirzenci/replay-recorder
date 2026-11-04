using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library.Player;

/// <summary>
/// The see-through control bar over fullscreen video: play/pause, a seek bar, the time, and leave
/// fullscreen. It is its own window because WPF can't draw over mpv's video window. It never takes
/// focus (so keyboard shortcuts keep going to the player) and hides after a few seconds of no mouse movement.
/// </summary>
sealed class FullscreenBar : Window
{
    const double BarHeight = 64, BarMargin = 28, HideAfterSeconds = 2.5;

    readonly MpvPlayer _mpv;
    readonly Slider _seek = new() { Minimum = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
    readonly TextBlock _time = new() { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    readonly TextBlock _playIcon = Icon("", 16);
    readonly Border _bar;
    readonly DispatcherTimer _mouseTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    POINT _lastCursor;
    DateTime _lastMove = DateTime.UtcNow;
    bool _updating, _dragging;

    /// <param name="gameVol">The player's own sliders: the bar's copies are bound to them, so the mix and its saving stay in one place.</param>
    public FullscreenBar(Window owner, MpvPlayer mpv, Action exitFullscreen, Slider gameVol, Slider micVol, bool mixEnabled)
    {
        _mpv = mpv;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;

        var play = RoundButton(_playIcon, "Play / pause (Space)".T(), () => _mpv.TogglePause());
        var exit = RoundButton(Icon("", 14), "Exit fullscreen (Esc)".T(), exitFullscreen);
        DockPanel.SetDock(play, Dock.Left);
        DockPanel.SetDock(exit, Dock.Right);
        DockPanel.SetDock(_time, Dock.Right);
        _time.Margin = new Thickness(0, 0, 12, 0);
        var mix = new StackPanel { Orientation = Orientation.Horizontal, IsEnabled = mixEnabled, Margin = new Thickness(0, 0, 8, 0),
            Children = { VolumeSlider("Game + chat".T(), gameVol), VolumeSlider("Your mic".T(), micVol) } };
        DockPanel.SetDock(mix, Dock.Right);

        _bar = new Border
        {
            Height = BarHeight,
            MaxWidth = 1100,
            Margin = new Thickness(BarMargin, 0, BarMargin, BarMargin),
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromArgb(150, 12, 12, 16)),
            Child = new DockPanel { LastChildFill = true, Children = { play, exit, mix, _time, _seek } },
        };
        Content = _bar;

        _seek.ValueChanged += (_, e) => { if (!_updating) _mpv.Seek(e.NewValue, exact: !_dragging); };
        _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _dragging = false; _mpv.Seek(_seek.Value); }));
        _mpv.StateChanged += Sync;
        Sync();

        _mouseTimer.Tick += (_, _) => AutoHide();
        _mouseTimer.Start();
        Closed += (_, _) => { _mouseTimer.Stop(); _mpv.StateChanged -= Sync; };
    }

    /// <summary>Covers the bottom of the owner window (call once it is fullscreen).</summary>
    public void PlaceOver(Window owner)
    {
        var src = PresentationSource.FromVisual(owner);
        if (src?.CompositionTarget == null) return;
        var topLeft = src.CompositionTarget.TransformFromDevice.Transform(owner.PointToScreen(new Point(0, 0)));
        double h = BarHeight + BarMargin * 2;
        Left = topLeft.X;
        Top = topLeft.Y + owner.ActualHeight - h;
        Width = owner.ActualWidth;
        Height = h;
    }

    void Sync()
    {
        _updating = true;
        if (_mpv.Duration > 0) _seek.Maximum = _mpv.Duration;
        if (!_dragging) _seek.Value = _mpv.Position;
        _updating = false;
        _time.Text = $"{TrimTimeline.Fmt(_mpv.Position)}  /  {TrimTimeline.Fmt(_mpv.Duration)}";
        _playIcon.Text = _mpv.Paused ? "" : "";
    }

    void AutoHide()
    {
        GetCursorPos(out var p);
        if (p.X != _lastCursor.X || p.Y != _lastCursor.Y) { _lastCursor = p; _lastMove = DateTime.UtcNow; }
        bool show = _dragging || _bar.IsMouseOver || _mpv.Paused || (DateTime.UtcNow - _lastMove).TotalSeconds < HideAfterSeconds;
        _bar.Visibility = show ? Visibility.Visible : Visibility.Hidden;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Clicking the bar must not take focus from the player, or Space / arrows / F / Esc stop working.
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    }

    static FrameworkElement VolumeSlider(string label, Slider source)
    {
        var s = new Slider { Minimum = source.Minimum, Maximum = source.Maximum, SmallChange = 1, LargeChange = 3, Width = 110, Focusable = false };
        s.SetBinding(RangeBase.ValueProperty, new System.Windows.Data.Binding(nameof(Slider.Value)) { Source = source, Mode = System.Windows.Data.BindingMode.TwoWay });
        s.MouseDoubleClick += (_, _) => source.Value = 0;
        return new StackPanel
        {
            Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 200)) }, s },
        };
    }

    static TextBlock Icon(string glyph, double size) => new()
    {
        Text = glyph, FontSize = size, Foreground = Brushes.White,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    static Button RoundButton(UIElement content, string tip, Action click)
    {
        var b = new Button
        {
            Content = content, ToolTip = tip, Width = 40, Height = 40, Focusable = false,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Application.Current.TryFindResource("RoundBtn") is Style s) b.Style = s;
        b.Click += (_, _) => click();
        return b;
    }

    const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000;
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}
