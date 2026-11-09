using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ReplayRecorder.Core;
using ReplayRecorder.Library.Player;

namespace ReplayRecorder.Library;

/// <summary>The clip player: opening clips, playback, trim and the game/mic mix.</summary>
public partial class MainWindow
{
    // ================================================================== player

    void OpenClip(ClipItem c)
    {
        _current = c;
        _edit = ClipEdits.Get(c.Path);
        LibraryView.Visibility = Visibility.Collapsed;
        PlayerView.Visibility = Visibility.Visible;
        PlayerTitle.Text = c.Title;
        PlayerSubtitle.Text = $"{c.Game}  ·  {c.Subtitle}";
        PlayerError.Visibility = Visibility.Collapsed;
        PreviewToggle.IsChecked = false;

        var tracks = c.Clip.SeparateTracks;
        MixPanel.IsEnabled = tracks != null;
        MixPanel.ToolTip = tracks == null ? "This clip has a single audio track".T() : null;
        _loadingClip = true;
        GameVol.Value = _edit.GameVolumeDb;
        MicVol.Value = _edit.MicVolumeDb;
        _loadingClip = false;
        UpdateVolumeLabels();

        Timeline.SetClip(c.Duration, _edit.In, _edit.EffectiveOut(c.Duration));
        Timeline.Position = 0;
        Timeline.SetLevels(null, null, ClipIndex.LevelsPerSecond);
        Timeline.SetGains(tracks != null ? GameVol.Value : 0, MicVol.Value);
        _ = LoadLevels(c);
        UpdateTrimText();
        UpdateTimeText(0);

        if (!EnsurePlayer()) return;
        _mpv!.SetLoop(null, null);
        _mpv.Load(c.Path, AudioGraph());
        Focus();
    }

    async Task LoadLevels(ClipItem c)
    {
        try
        {
            var levels = await _index.EnsureLevelsAsync(c.Clip, _cts.Token);
            if (levels is var (game, mic) && _current == c) Timeline.SetLevels(game, mic, ClipIndex.LevelsPerSecond);
        }
        catch (OperationCanceledException) { }
    }

    bool EnsurePlayer()
    {
        if (_mpv != null) return true;
        try
        {
            if (Video.Hwnd == IntPtr.Zero) UpdateLayout();
            _mpv = new MpvPlayer(Video.Hwnd);
            _mpv.StateChanged += OnPlayerState;
            _mpv.FileLoaded += OnFileLoaded;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Player start failed", ex);
            PlayerError.Text = ex is FileNotFoundException ? Ffmpeg.MpvInstallHint.T() : "The player couldn't start: {0}".T(ex.Message);
            PlayerError.Visibility = Visibility.Visible;
            return false;
        }
    }

    void OnFileLoaded()
    {
        if (_current == null || _mpv == null) return;
        // Duration from the player is exact; prefer it over the probe.
        if (_mpv.Duration > 0 && Math.Abs(_mpv.Duration - Timeline.Duration) > 0.05)
            Timeline.SetClip(_mpv.Duration, _edit.In, _edit.EffectiveOut(_mpv.Duration));
        // Start where the trimmed clip starts.
        if (_edit.In > 0) _mpv.Seek(_edit.In);
        UpdateTrimText();
    }

    void OnPlayerState()
    {
        if (_mpv == null) return;
        // mpv may report the length after "file-loaded"; whenever it does, it wins over the probe.
        if (_current != null && _mpv.Duration > 0 && Math.Abs(_mpv.Duration - Timeline.Duration) > 0.05)
        {
            Timeline.SetClip(_mpv.Duration, _edit.In, _edit.EffectiveOut(_mpv.Duration));
            UpdateTrimText();
        }
        Timeline.Position = _mpv.Position;
        UpdateTimeText(_mpv.Position);
        PlayIcon.Text = _mpv.Paused ? "" : "";
    }

    void CloseClip()
    {
        SetFullscreen(false);
        _mpv?.SetLoop(null, null);
        _mpv?.Stop();
        _current = null;
        PlayerView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        ClipGrid.Focus();
        ScheduleViewUpdate();
    }

    /// <summary>mpv filter graph that mixes the separate game and mic tracks with the chosen volumes.</summary>
    string? AudioGraph()
    {
        if (_current?.Clip.SeparateTracks is not { } tracks) return null;
        var (g, m) = tracks;
        string Db(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        return $"[aid{g + 1}]volume={Db(GameVol.Value)}dB[ga];" +
               $"[aid{m + 1}]volume={Db(MicVol.Value)}dB,aformat=channel_layouts=stereo[ma];" +
               "[ga][ma]amix=inputs=2:normalize=0,alimiter=limit=0.95:level=0[ao];[vid1]null[vo]";
    }

    void TrimEdited()
    {
        if (_current == null) return;
        ClipEdits.Set(_current.Path, _edit);
        UpdateTrimText();
        if (PreviewToggle.IsChecked == true) _mpv?.SetLoop(_edit.In, _edit.EffectiveOut(Timeline.Duration));
    }

    void UpdateTrimText()
    {
        double dur = Timeline.Duration;
        double len = _edit.EffectiveOut(dur) - _edit.In;
        TrimText.Text = _edit.IsTrimmed(dur) ? "Trimmed: {0} of {1}".T(TrimTimeline.Fmt(len), TrimTimeline.Fmt(dur)) : "";
    }

    void UpdateTimeText(double t) =>
        TimeText.Text = $"{TrimTimeline.Fmt(t)}.{(int)(Math.Max(0, t) * 10 % 10)}  /  {TrimTimeline.Fmt(Timeline.Duration)}";

    void UpdateVolumeLabels()
    {
        static string L(double v) => v == 0 ? "0 dB" : $"{v:+0;-0} dB";
        GameVolText.Text = L(GameVol.Value);
        MicVolText.Text = L(MicVol.Value);
    }

    void SetIn()
    {
        if (_mpv == null) return;
        _edit.In = Math.Min(_mpv.Position, _edit.EffectiveOut(Timeline.Duration) - 0.5);
        if (_edit.In < 0) _edit.In = 0;
        Timeline.SetClip(Timeline.Duration, _edit.In, _edit.EffectiveOut(Timeline.Duration));
        TrimEdited();
        TogglePreview(true);
    }

    void SetOut()
    {
        if (_mpv == null) return;
        _edit.Out = Math.Max(_mpv.Position, _edit.In + 0.5);
        Timeline.SetClip(Timeline.Duration, _edit.In, _edit.Out);
        TrimEdited();
        TogglePreview(true);
    }

    void TogglePreview(bool? on = null)
    {
        bool enable = on ?? PreviewToggle.IsChecked != true;
        PreviewToggle.IsChecked = enable;
        if (_mpv == null) return;
        if (enable)
        {
            _mpv.SetLoop(_edit.In, _edit.EffectiveOut(Timeline.Duration));
            _mpv.Seek(_edit.In);
            _mpv.SetPause(false);
        }
        else _mpv.SetLoop(null, null);
    }

    void Step(int dir)
    {
        if (_mpv == null) return;
        if (!_mpv.Paused) _mpv.SetPause(true);
        if (dir > 0) _mpv.FrameStep(); else _mpv.FrameBackStep();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PlayerView.Visibility != Visibility.Visible)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); e.Handled = true; }
            return;
        }
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // Fullscreen is for watching: no trimming, exporting or deleting from there.
        if (_fullscreen != null && e.Key is Key.I or Key.O or Key.P or Key.E or Key.Delete) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Space: _mpv?.TogglePause(); break;
            case Key.Left when shift: _mpv?.SeekBy(-5); break;
            case Key.Right when shift: _mpv?.SeekBy(5); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(+1); break;
            case Key.J: _mpv?.SeekBy(-10); break;
            case Key.L: _mpv?.SeekBy(10); break;
            case Key.I: SetIn(); break;
            case Key.O: SetOut(); break;
            case Key.P: TogglePreview(); break;
            case Key.Home: _mpv?.Seek(_edit.In); break;
            case Key.End: _mpv?.Seek(Math.Max(_edit.In, _edit.EffectiveOut(Timeline.Duration) - 0.05)); break;
            case Key.E: _ = ExportCurrent(); break;
            case Key.F: SetFullscreen(_fullscreen == null); break;
            case Key.Escape: if (_fullscreen != null) SetFullscreen(false); else CloseClip(); break;
            case Key.Delete: if (_current != null) DeleteClips(new() { _current }); break;
            default: return;
        }
        e.Handled = true;
    }

    void Back_Click(object sender, RoutedEventArgs e) => CloseClip();
    void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(_fullscreen == null);

    // Window state before fullscreen, or null when not fullscreen.
    (WindowState State, WindowStyle Style, ResizeMode Resize)? _fullscreen;
    FullscreenBar? _fsBar;

    /// <summary>Video only, edge to edge. Keyboard shortcuts keep working; F or Esc comes back.</summary>
    void SetFullscreen(bool on)
    {
        if (on == (_fullscreen != null)) return;
        var hide = on ? Visibility.Collapsed : Visibility.Visible;
        foreach (var el in new UIElement[] { PlayerHeader, Timeline, PlayerControls, PlayerTopPanel, PlayerBottomPanel }) el.Visibility = hide;
        VideoFrame.Margin = on ? new Thickness(0) : new Thickness(20, 0, 20, 20);
        if (on)
        {
            _fullscreen = (WindowState, WindowStyle, ResizeMode);
            WindowState = WindowState.Normal; // Maximized with a border would leave the taskbar showing
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            if (_mpv != null)
            {
                _fsBar = new FullscreenBar(this, _mpv, () => SetFullscreen(false), GameVol, MicVol, MixPanel.IsEnabled);
                // Placed once the window has finished growing to the full screen.
                Dispatcher.BeginInvoke(() => { if (_fsBar == null) return; _fsBar.PlaceOver(this); _fsBar.Show(); }, DispatcherPriority.Loaded);
            }
        }
        else
        {
            _fsBar?.Close();
            _fsBar = null;
            var (state, style, resize) = _fullscreen!.Value;
            _fullscreen = null;
            WindowStyle = style;
            ResizeMode = resize;
            WindowState = state;
        }
    }

    /// <summary>Too narrow for the mix sliders + export beside the centred transport: move them to a row below.</summary>
    void PlayerControls_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RightControls.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double room = (PlayerControls.ActualWidth - Transport.ActualWidth - Transport.Margin.Left - Transport.Margin.Right) / 2;
        bool below = RightControls.DesiredSize.Width > room;
        Grid.SetRow(RightControls, below ? 1 : 0);
        Grid.SetColumn(RightControls, below ? 0 : 2);
        Grid.SetColumnSpan(RightControls, below ? 3 : 1);
        RightControls.Margin = new Thickness(0, below ? 12 : 0, 0, 0);
    }
    void Play_Click(object sender, RoutedEventArgs e) => _mpv?.TogglePause();
    void StepBack_Click(object sender, RoutedEventArgs e) => Step(-1);
    void StepFwd_Click(object sender, RoutedEventArgs e) => Step(+1);
    void SetIn_Click(object sender, RoutedEventArgs e) => SetIn();
    void SetOut_Click(object sender, RoutedEventArgs e) => SetOut();
    void Preview_Click(object sender, RoutedEventArgs e) => TogglePreview(PreviewToggle.IsChecked == true);
    void PlayerShow_Click(object sender, RoutedEventArgs e) { if (_current != null) ShowInFolder(_current.Path); }
    void PlayerDelete_Click(object sender, RoutedEventArgs e) { if (_current != null) DeleteClips(new() { _current }); }

    void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        _edit.In = 0; _edit.Out = 0;
        Timeline.SetClip(Timeline.Duration, 0, Timeline.Duration);
        TrimEdited();
        TogglePreview(false);
    }

    void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _loadingClip) return;
        if (sender is Slider s) s.Value = Math.Round(s.Value);
        UpdateVolumeLabels();
        if (_current == null) return;
        _edit.GameVolumeDb = GameVol.Value;
        _edit.MicVolumeDb = MicVol.Value;
        Timeline.SetGains(GameVol.Value, MicVol.Value);
        ClipEdits.Set(_current.Path, _edit);
        _mixDebounce.Stop(); _mixDebounce.Start();
    }

    void GameVol_DoubleClick(object sender, MouseButtonEventArgs e) => GameVol.Value = 0;
    void MicVol_DoubleClick(object sender, MouseButtonEventArgs e) => MicVol.Value = 0;
}
