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
        App.TranslateTree(this);
        ContentRendered += (_, _) => App.TranslateTree(this); // template parts exist only once shown
        foreach (var t in new[] { SubGame, SubMic }) t.Text = t.Text.ToUpper(Lang.Culture);
        NavList.SelectedIndex = 0; // first page
        SourceInitialized += (_, _) => App.UseDarkTitleBar(this);
        Load();
        _meterTimer.Tick += (_, _) => UpdateMeters();
        Loaded += async (_, _) => { _meterTimer.Start(); await LoadDevicesAsync(); };
        Closed += (_, _) =>
        {
            _meterTimer.Stop(); OnMeterThread(() => { _sysMeter.Dispose(); _micMeter.Dispose(); });
            if (DialogResult != true) App.ApplyTheme(_s.Theme, _s.AccentColor); // undo the live preview
        };
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
        var monitorChoices = new List<Choice<string>> { new("", primary != null ? "Automatic: primary monitor ({0})".T(primary.Label.Split(" · ")[0]) : "Automatic: primary monitor".T()) };
        monitorChoices.AddRange(_displays.Select(d => new Choice<string>(d.DeviceName, d.Label)));
        MonitorBox.ItemsSource = monitorChoices;
        MonitorBox.SelectedIndex = Math.Max(0, monitorChoices.FindIndex(c => c.Value.Length > 0 &&
            string.Equals(c.Value, _s.Monitor, StringComparison.OrdinalIgnoreCase)));

        Fill(ResolutionBox, _s.Height, (0, "Native (up to 1440p) · recommended"), (1080, "1080p"), (720, "720p"));
        Fill(FpsBox, _s.Fps, (60, "60 fps"), (30, "30 fps"), (0, "Native: your screen's refresh rate (bigger files)"));
        Fill(QualityBox, _s.Quality, (19, "High (bigger files)"), (21, "Balanced · recommended"), (25, "Smaller files"));
        Fill(EncoderBox, _s.Encoder, (EncoderBackend.Auto, "Automatic (NVENC on the NVIDIA GPU)"), (EncoderBackend.Nvenc, "NVIDIA NVENC"),
            (EncoderBackend.Qsv, "Intel Quick Sync"), (EncoderBackend.X264, "CPU x264 (heavy, last resort)"));
        Fill(CodecBox, _s.Codec, (VideoCodec.H264, "H.264 · plays everywhere · recommended"), (VideoCodec.Hevc, "HEVC (smaller, less compatible)"));
        Fill(CaptureBox, _s.Capture, (CaptureMethod.Wgc, "Windows Graphics Capture · recommended"), (CaptureMethod.Dda, "Desktop Duplication (native resolution only)"));
        CursorBox.IsChecked = _s.CaptureCursor;

        SysEnabled.IsChecked = _s.SystemAudioEnabled;
        // Audio devices are listed after the window is up (LoadDevicesAsync): Windows can take
        // seconds to enumerate them. Until then the saved choice stands in, so Save keeps it.
        SysDeviceBox.ItemsSource = new[] { new DeviceChoice(_s.SystemAudioDeviceId, "Loading devices…".T()) };
        SysDeviceBox.SelectedIndex = 0;
        SysVol.Value = _s.SystemVolumeDb;
        DiscordSeparate.IsChecked = _s.DiscordSeparate;
        DiscordVol.Value = _s.DiscordVolumeDb;
        MicEnabled.IsChecked = _s.MicEnabled;
        MicDeviceBox.ItemsSource = new[] { new DeviceChoice(_s.MicDeviceId, "Loading devices…".T()) };
        MicDeviceBox.SelectedIndex = 0;
        MicVol.Value = _s.MicVolumeDb;
        Fill(MicLevelingBox, _s.MicLevelingStrength, (MicLeveling.Off, "Off"), (MicLeveling.Light, "Light"),
            (MicLeveling.Medium, "Medium"), (MicLeveling.Strong, "Strong"));

        ClipsFolderBox.Text = _s.ClipsFolder;
        ExportFolderBox.Text = _s.ExportFolder;
        BufferFolderBox.Text = _s.BufferFolder;
        LimitBox.Text = _s.ClipsFolderLimitGB.ToString("0.#", CultureInfo.CurrentCulture);
        AutoDeleteBox.IsChecked = _s.AutoDelete;
        AutoDeleteDaysBox.Text = _s.AutoDeleteDays.ToString(CultureInfo.CurrentCulture);
        TargetBox.Text = _s.DiscordTargetMB.ToString("0.#", CultureInfo.CurrentCulture);
        _ = ShowUsageAsync();

        StartupBox.IsChecked = Autostart.IsEnabled;
        NotifyBox.IsChecked = _s.ShowNotifications;
        SoundBox.IsChecked = _s.PlaySaveSound;
        Fill(LanguageBox, _s.Language, ("en", "English"), ("tr", "Türkçe"));
        Fill(ThemeBox, _s.Theme == "booth" ? "amber" : _s.Theme, ("aurora", "Modern"), ("windows", "Classic (grey)"), ("amber", "Classic (blue-grey)"));
        _accent = _s.AccentColor;
        foreach (var rb in AccentSwatches.Children.OfType<RadioButton>())
            rb.IsChecked = string.Equals((string)rb.Tag, _accent, StringComparison.OrdinalIgnoreCase);
        ThemeBox.SelectionChanged += (_, _) => PreviewLook();
        RamBox.IsChecked = _s.BufferInRam;
        GamesBox.Text = string.Join(Environment.NewLine, _s.GameNames.OrderBy(k => k.Value).Select(kv => $"{kv.Key} = {kv.Value}"));
        UpdateLabels();
    }

    static void Fill<T>(ComboBox box, T current, params (T Value, string Label)[] options)
    {
        var items = options.Select(o => new Choice<T>(o.Value, o.Label.T())).ToList();
        box.ItemsSource = items;
        box.SelectedIndex = Math.Max(0, items.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, current)));
    }

    static T Get<T>(ComboBox box, T fallback) => box.SelectedItem is Choice<T> c ? c.Value : fallback;

    async Task ShowUsageAsync()
    {
        var folder = _s.ClipsFolder;
        long bytes = await Task.Run(() => Paths.FolderSize(folder));
        UsageText.Text = "Using {0:0.0} GB right now".T(bytes / 1e9);
    }

    // ------------------------------------------------------------------ live bits

    void UpdateLabels()
    {
        if (BufferText == null) return;
        int b = (int)(Math.Round(BufferSlider.Value / 5) * 5);
        BufferText.Text = b >= 60 ? "{0}:{1:00} min".T(b / 60, b % 60) : "{0} s".T(b);
        // ~3.5 MB/s measured at 1440p60 on the desktop, up to the bitrate cap in busy games.
        int lo = (int)Math.Round((b + 6) * 3.5 / 10) * 10, hi = (int)Math.Round((b + 6) * 6.25 / 10) * 10;
        RamHint.Text = "No constant SSD writes. Uses about {0}–{1} MB of RAM for {2}.".T(lo, hi, BufferText.Text);
        static string Db(double v) => Math.Round(v) == 0 ? "0 dB" : $"{Math.Round(v):+0;-0} dB";
        SysVolText.Text = Db(SysVol.Value);
        MicVolText.Text = Db(MicVol.Value);
        DiscordVolText.Text = Db(DiscordVol.Value);

        var sel = MonitorBox.SelectedItem as Choice<string>;
        var d = sel == null ? null : sel.Value.Length == 0 ? _displays.FirstOrDefault(x => x.Primary) : _displays.FirstOrDefault(x => x.DeviceName == sel.Value);
        string? warn = d == null ? null
            : !d.OnNvidia ? "This screen is driven by {0}, not the RTX GPU (hybrid/Optimus mode). Replay Recorder will use Intel Quick Sync; for best results switch the MUX to discrete-GPU mode.".T(d.AdapterName)
            : d.HdrEnabled ? "HDR is on for this screen. Clips are recorded in SDR, so very bright highlights may clip.".T()
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
            bar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, db > -3 ? "Bad" : db > -12 ? "Warn" : "Good");
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
        if (!TryNumber(LimitBox.Text, 0, 100000, out var limit)) { Error("Storage warning must be a number of GB (0 = off).".T()); return; }
        if (!TryNumber(AutoDeleteDaysBox.Text, 1, 3650, out var days)) { Error("Auto-delete needs a number of days (1 or more).".T()); return; }
        if (!TryNumber(TargetBox.Text, 1, 500, out var target)) { Error("Maximum file size must be between 1 and 500 MB.".T()); return; }
        if (string.IsNullOrWhiteSpace(SaveHotkey.Value)) { Error("Set a save-clip hotkey.".T()); return; }
        if (SaveHotkey.Value == RecordHotkey.Value) { Error("The two hotkeys must be different.".T()); return; }
        foreach (var (box, name) in new[] { (ClipsFolderBox, "Clips folder can't be created: {0}"), (ExportFolderBox, "Discord exports folder can't be created: {0}"), (BufferFolderBox, "Replay buffer folder can't be created: {0}") })
        {
            try { Directory.CreateDirectory(box.Text.Trim()); }
            catch { Error(name.T(box.Text)); return; }
        }
        if (Path.GetFullPath(ExportFolderBox.Text.Trim()).StartsWith(Path.GetFullPath(ClipsFolderBox.Text.Trim()) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Error("Put the Discord exports folder outside the clips folder, so exports don't show up as clips.".T()); return;
        }
        // Newly on, or fewer days: say how many clips the next round deletes before it happens.
        if (AutoDeleteBox.IsChecked == true && (!_s.AutoDelete || days < _s.AutoDeleteDays))
        {
            var due = AutoDelete.Due(new ClipIndex(ClipsFolderBox.Text.Trim()).Scan(), (int)Math.Round(days), p => ClipEdits.Get(p).Favorite, DateTime.Now);
            if (due.Count > 0 && MessageBox.Show(this,
                    (due.Count == 1 ? "Auto-delete will delete 1 clip older than {0} days for good. Favorites are kept.\n\nTurn it on?"
                                    : "Auto-delete will delete {1} clips older than {0} days for good. Favorites are kept.\n\nTurn it on?").T((int)Math.Round(days), due.Count),
                    "Auto-delete".T(), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
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
        _s.DiscordSeparate = DiscordSeparate.IsChecked == true;
        _s.DiscordVolumeDb = Math.Round(DiscordVol.Value);
        _s.MicEnabled = MicEnabled.IsChecked == true;
        _s.MicDeviceId = (MicDeviceBox.SelectedItem as DeviceChoice)?.Id ?? "";
        _s.MicVolumeDb = Math.Round(MicVol.Value);
        _s.MicLevelingStrength = Get(MicLevelingBox, MicLeveling.Medium);
        _s.ClipsFolder = ClipsFolderBox.Text.Trim();
        _s.ExportFolder = ExportFolderBox.Text.Trim();
        _s.BufferFolder = BufferFolderBox.Text.Trim();
        _s.ClipsFolderLimitGB = limit;
        _s.AutoDelete = AutoDeleteBox.IsChecked == true;
        _s.AutoDeleteDays = (int)Math.Round(days);
        _s.DiscordTargetMB = target;
        _s.ShowNotifications = NotifyBox.IsChecked == true;
        _s.PlaySaveSound = SoundBox.IsChecked == true;
        _s.Language = Get(LanguageBox, "en");
        _s.Theme = Get(ThemeBox, "aurora");
        _s.AccentColor = _accent;
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
        catch (Exception ex) { Error("Couldn't save settings: {0}".T(ex.Message)); }
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

    // ---- own title bar (WindowChrome in the XAML), as in the main window
    void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    /// <summary>A maximised window hangs over the screen edges by its resize frame; pad it back in.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        Root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxBtn.Content = max ? "\uE923" : "\uE922";
        MaxBtn.ToolTip = (max ? "Restore" : "Maximize").T();
    }

    // ---- Look and accent colour: previewed live, saved with Save, undone on Cancel
    string _accent = "";
    void PreviewLook() => App.ApplyTheme(Get(ThemeBox, "aurora"), _accent);

    void Swatch_Checked(object sender, RoutedEventArgs e)
    {
        _accent = (string)((FrameworkElement)sender).Tag;
        if (IsLoaded) PreviewLook(); // not while Load() ticks the saved one
    }

    void CustomAccent_Click(object sender, RoutedEventArgs e)
    {
        var picked = ColorPicker.Ask(this, ((System.Windows.Media.SolidColorBrush)FindResource("Accent")).Color);
        if (picked == null) return;
        foreach (var rb in AccentSwatches.Children.OfType<RadioButton>()) rb.IsChecked = false;
        _accent = picked;
        PreviewLook();
    }

    /// <summary>Section menu: shows only the chosen section. Each heading (x:Name "Sec...") starts a page that runs to the next heading.</summary>
    void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not ListBoxItem { Tag: string page }) return;
        string current = "";
        foreach (FrameworkElement el in Pages.Children)
        {
            if (el.Name.StartsWith("Sec")) current = el.Name;
            el.Visibility = current == page ? Visibility.Visible : Visibility.Collapsed;
        }
        Scroller.ScrollToTop();
    }
}
