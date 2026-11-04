using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library.SettingsUi;

public partial class SettingsWindow : Window
{
    readonly Settings _s = Settings.Load();
    readonly LevelMeter _sysMeter = new(), _micMeter = new();
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    List<DisplayInfo> _displays = new();

    record Choice<T>(T Value, string Label) { public override string ToString() => Label; }

    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => App.UseDarkTitleBar(this);
        Load();
        _meterTimer.Tick += (_, _) => UpdateMeters();
        Loaded += async (_, _) => { _meterTimer.Start(); await LoadDevicesAsync(); };
        Closed += (_, _) => { _meterTimer.Stop(); OnMeterThread(() => { _sysMeter.Dispose(); _micMeter.Dispose(); }); };
    }

    void Load()
    {
        BufferSlider.Value = _s.BufferSeconds;
        SaveHotkey.Value = _s.SaveClipHotkey;
        RecordHotkey.Value = _s.ToggleRecordingHotkey;

        // Index 0 = automatic (primary). \\.\DISPLAYn numbers can change when monitors are
        // re-plugged, so pinning one is only done when the user picks it explicitly.
        _displays = Displays.Enumerate();
        var primary = _displays.FirstOrDefault(d => d.Primary);
        var monitorChoices = new List<Choice<string>> { new("", $"Automatic: primary monitor{(primary != null ? $" ({primary.Label.Split(" · ")[0]})" : "")}") };
        monitorChoices.AddRange(_displays.Select(d => new Choice<string>(d.DeviceName, d.Label)));
        MonitorBox.ItemsSource = monitorChoices;
        MonitorBox.SelectedIndex = Math.Max(0, monitorChoices.FindIndex(c => c.Value.Length > 0 &&
            string.Equals(c.Value, _s.Monitor, StringComparison.OrdinalIgnoreCase)));

        Fill(ResolutionBox, _s.Height, (0, "Native (up to 1440p) · recommended"), (1080, "1080p"), (720, "720p"));
        Fill(FpsBox, _s.Fps, (60, "60 fps"), (30, "30 fps"));
        Fill(QualityBox, _s.Quality, (19, "High (bigger files)"), (21, "Balanced · recommended"), (25, "Smaller files"));
        Fill(EncoderBox, _s.Encoder, (EncoderBackend.Auto, "Automatic (NVENC on the NVIDIA GPU)"), (EncoderBackend.Nvenc, "NVIDIA NVENC"),
            (EncoderBackend.Qsv, "Intel Quick Sync"), (EncoderBackend.X264, "CPU x264 (heavy, last resort)"));
        Fill(CodecBox, _s.Codec, (VideoCodec.H264, "H.264 · plays everywhere · recommended"), (VideoCodec.Hevc, "HEVC (smaller, less compatible)"));
        Fill(CaptureBox, _s.Capture, (CaptureMethod.Wgc, "Windows Graphics Capture · recommended"), (CaptureMethod.Dda, "Desktop Duplication (native resolution only)"));
        CursorBox.IsChecked = _s.CaptureCursor;

        SysEnabled.IsChecked = _s.SystemAudioEnabled;
        // Audio devices are listed after the window is up (LoadDevicesAsync): Windows can take
        // seconds to enumerate them. Until then the saved choice stands in, so Save keeps it.
        SysDeviceBox.ItemsSource = new[] { new DeviceChoice(_s.SystemAudioDeviceId, "Loading devices…") };
        SysDeviceBox.SelectedIndex = 0;
        SysVol.Value = _s.SystemVolumeDb;
        MicEnabled.IsChecked = _s.MicEnabled;
        MicDeviceBox.ItemsSource = new[] { new DeviceChoice(_s.MicDeviceId, "Loading devices…") };
        MicDeviceBox.SelectedIndex = 0;
        MicVol.Value = _s.MicVolumeDb;
        Fill(MicLevelingBox, _s.MicLevelingStrength, (MicLeveling.Off, "Off"), (MicLeveling.Light, "Light"),
            (MicLeveling.Medium, "Medium"), (MicLeveling.Strong, "Strong"));

        ClipsFolderBox.Text = _s.ClipsFolder;
        ExportFolderBox.Text = _s.ExportFolder;
        BufferFolderBox.Text = _s.BufferFolder;
        LimitBox.Text = _s.ClipsFolderLimitGB.ToString("0.#", CultureInfo.CurrentCulture);
        TargetBox.Text = _s.DiscordTargetMB.ToString("0.#", CultureInfo.CurrentCulture);
        _ = ShowUsageAsync();

        StartupBox.IsChecked = Autostart.IsEnabled;
        NotifyBox.IsChecked = _s.ShowNotifications;
        SoundBox.IsChecked = _s.PlaySaveSound;
        RamBox.IsChecked = _s.BufferInRam;
        GamesBox.Text = string.Join(Environment.NewLine, _s.GameNames.OrderBy(k => k.Value).Select(kv => $"{kv.Key} = {kv.Value}"));
        UpdateLabels();
    }

    static void Fill<T>(ComboBox box, T current, params (T Value, string Label)[] options)
    {
        var items = options.Select(o => new Choice<T>(o.Value, o.Label)).ToList();
        box.ItemsSource = items;
        box.SelectedIndex = Math.Max(0, items.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, current)));
    }

    static T Get<T>(ComboBox box, T fallback) => box.SelectedItem is Choice<T> c ? c.Value : fallback;

    async Task ShowUsageAsync()
    {
        var folder = _s.ClipsFolder;
        long bytes = await Task.Run(() => Paths.FolderSize(folder));
        UsageText.Text = $"Using {bytes / 1e9:0.0} GB right now";
    }

    // ------------------------------------------------------------------ live bits

    void UpdateLabels()
    {
        if (BufferText == null) return;
        int b = (int)(Math.Round(BufferSlider.Value / 5) * 5);
        BufferText.Text = b >= 60 ? $"{b / 60}:{b % 60:00} min" : $"{b} s";
        // ~3.5 MB/s measured at 1440p60 on the desktop, up to the bitrate cap in busy games.
        int lo = (int)Math.Round((b + 6) * 3.5 / 10) * 10, hi = (int)Math.Round((b + 6) * 6.25 / 10) * 10;
        RamHint.Text = $"No constant SSD writes. Uses about {lo}–{hi} MB of RAM for {BufferText.Text}.";
        static string Db(double v) => Math.Round(v) == 0 ? "0 dB" : $"{Math.Round(v):+0;-0} dB";
        SysVolText.Text = Db(SysVol.Value);
        MicVolText.Text = Db(MicVol.Value);

        var sel = MonitorBox.SelectedItem as Choice<string>;
        var d = sel == null ? null : sel.Value.Length == 0 ? _displays.FirstOrDefault(x => x.Primary) : _displays.FirstOrDefault(x => x.DeviceName == sel.Value);
        string? warn = d == null ? null
            : !d.OnNvidia ? $"This screen is driven by {d.AdapterName}, not the RTX GPU (hybrid/Optimus mode). Replay Recorder will use Intel Quick Sync; for best results switch the MUX to discrete-GPU mode."
            : d.HdrEnabled ? "HDR is on for this screen. Clips are recorded in SDR, so very bright highlights may clip."
            : null;
        MonitorWarning.Text = warn ?? "";
        MonitorWarning.Visibility = warn == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void BufferSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    void Vol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();
    void MonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLabels();
    bool _devicesReady;
    Task _meterChain = Task.CompletedTask;

    async Task LoadDevicesAsync()
    {
        var (outs, mics) = await Task.Run(() => (AudioDevices.Outputs(), AudioDevices.Mics()));
        if (!IsVisible) return;
        SysDeviceBox.ItemsSource = outs;
        SysDeviceBox.SelectedIndex = Math.Max(0, outs.FindIndex(o => o.Id == _s.SystemAudioDeviceId));
        MicDeviceBox.ItemsSource = mics;
        MicDeviceBox.SelectedIndex = Math.Max(0, mics.FindIndex(m => m.Id == _s.MicDeviceId));
        _devicesReady = true;
        RestartMeters();
    }

    void SysDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_devicesReady) RestartMeters(); }
    void MicDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_devicesReady) RestartMeters(); }

    // Opening a WASAPI capture can block for seconds, so meters start and stop on a background
    // chain (one at a time, in order) instead of freezing the window.
    void OnMeterThread(Action a) => _meterChain = _meterChain.ContinueWith(_ => { try { a(); } catch { } }, TaskScheduler.Default);

    void RestartMeters()
    {
        var sysId = (SysDeviceBox.SelectedItem as DeviceChoice)?.Id ?? "";
        var micId = (MicDeviceBox.SelectedItem as DeviceChoice)?.Id ?? "";
        OnMeterThread(() =>
        {
            _sysMeter.Start(sysId, loopback: true);
            _micMeter.Start(micId, loopback: false);
        });
    }

    void UpdateMeters()
    {
        Show(SysMeter, SysMeterText, _sysMeter.ReadDb() + SysVol.Value);
        Show(MicMeter, MicMeterText, _micMeter.ReadDb() + MicVol.Value);

        static void Show(System.Windows.Controls.Border bar, TextBlock label, double db)
        {
            db = Math.Min(0, db);
            double f = Math.Clamp((db + 60) / 60, 0, 1); // -60..0 dBFS
            bar.Width = f * ((FrameworkElement)bar.Parent).ActualWidth;
            bar.Background = new System.Windows.Media.SolidColorBrush(db > -3 ? System.Windows.Media.Color.FromRgb(0xE5, 0x53, 0x4B)
                : db > -12 ? System.Windows.Media.Color.FromRgb(0xE5, 0xA8, 0x4B) : System.Windows.Media.Color.FromRgb(0x2E, 0xCC, 0x71));
            label.Text = db <= -59 ? "silent" : $"{db:0} dB";
        }
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)FindName((string)((FrameworkElement)sender).Tag);
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : null };
        if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
    }

    // ------------------------------------------------------------------ save

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNumber(LimitBox.Text, 0, 100000, out var limit)) { Error("Storage warning must be a number of GB (0 = off)."); return; }
        if (!TryNumber(TargetBox.Text, 1, 500, out var target)) { Error("Maximum file size must be between 1 and 500 MB."); return; }
        if (string.IsNullOrWhiteSpace(SaveHotkey.Value)) { Error("Set a save-clip hotkey."); return; }
        if (SaveHotkey.Value == RecordHotkey.Value) { Error("The two hotkeys must be different."); return; }
        foreach (var (box, name) in new[] { (ClipsFolderBox, "Clips"), (ExportFolderBox, "Discord exports"), (BufferFolderBox, "Replay buffer") })
        {
            try { Directory.CreateDirectory(box.Text.Trim()); }
            catch { Error($"{name} folder can't be created: {box.Text}"); return; }
        }
        if (Path.GetFullPath(ExportFolderBox.Text.Trim()).StartsWith(Path.GetFullPath(ClipsFolderBox.Text.Trim()) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Error("Put the Discord exports folder outside the clips folder, so exports don't show up as clips."); return;
        }

        _s.BufferSeconds = (int)(Math.Round(BufferSlider.Value / 5) * 5);
        _s.SaveClipHotkey = SaveHotkey.Value;
        _s.ToggleRecordingHotkey = RecordHotkey.Value;
        _s.Monitor = MonitorBox.SelectedItem is Choice<string> m ? m.Value : "";
        _s.Height = Get(ResolutionBox, 0);
        _s.Fps = Get(FpsBox, 60);
        _s.Quality = Get(QualityBox, 21);
        _s.Encoder = Get(EncoderBox, EncoderBackend.Auto);
        _s.Codec = Get(CodecBox, VideoCodec.H264);
        _s.Capture = Get(CaptureBox, CaptureMethod.Wgc);
        _s.CaptureCursor = CursorBox.IsChecked == true;
        _s.SystemAudioEnabled = SysEnabled.IsChecked == true;
        _s.SystemAudioDeviceId = (SysDeviceBox.SelectedItem as DeviceChoice)?.Id ?? "";
        _s.SystemVolumeDb = Math.Round(SysVol.Value);
        _s.MicEnabled = MicEnabled.IsChecked == true;
        _s.MicDeviceId = (MicDeviceBox.SelectedItem as DeviceChoice)?.Id ?? "";
        _s.MicVolumeDb = Math.Round(MicVol.Value);
        _s.MicLevelingStrength = Get(MicLevelingBox, MicLeveling.Medium);
        _s.ClipsFolder = ClipsFolderBox.Text.Trim();
        _s.ExportFolder = ExportFolderBox.Text.Trim();
        _s.BufferFolder = BufferFolderBox.Text.Trim();
        _s.ClipsFolderLimitGB = limit;
        _s.DiscordTargetMB = target;
        _s.ShowNotifications = NotifyBox.IsChecked == true;
        _s.PlaySaveSound = SoundBox.IsChecked == true;
        _s.BufferInRam = RamBox.IsChecked == true;
        _s.StartWithWindows = StartupBox.IsChecked == true;
        _s.GameNames = ParseGames(GamesBox.Text);

        try
        {
            Autostart.Set(_s.StartWithWindows);
            _s.Save(); // the recorder watches this file and restarts capture with the new settings
            Log.Info("Settings saved from the settings window");
            DialogResult = true;
        }
        catch (Exception ex) { Error($"Couldn't save settings: {ex.Message}"); }
    }

    static Dictionary<string, string> ParseGames(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var exe = line[..i].Trim();
            var name = line[(i + 1)..].Trim();
            if (exe.Length > 0 && name.Length > 0) d[exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe"] = name;
        }
        return d;
    }

    static bool TryNumber(string s, double min, double max, out double v) =>
        (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v) || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
        && v >= min && v <= max;

    void Error(string msg) => MessageBox.Show(this, msg, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
