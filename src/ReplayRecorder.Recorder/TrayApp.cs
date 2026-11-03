using System.Diagnostics;
using System.Drawing.Drawing2D;
using ReplayRecorder.Core;
using Microsoft.Win32;

namespace ReplayRecorder.Recorder;

/// <summary>Tray icon, menu and hotkeys. There is no window while recording.</summary>
sealed class TrayApp : ApplicationContext
{
    static readonly Color Green = Color.FromArgb(46, 204, 113);
    static readonly Color Red = Color.FromArgb(231, 76, 60);
    static readonly Color Grey = Color.FromArgb(140, 140, 150);

    Settings _settings;
    ReplayEngine _engine;
    readonly NotifyIcon _tray;
    readonly Hotkeys _hotkeys = new();
    readonly SynchronizationContext _ui;
    readonly ToolStripMenuItem _saveItem, _recordItem, _pauseItem, _startupItem, _statusItem, _updateItem;
    UpdateInfo? _update;
    System.Threading.Timer? _updateTimer;
    readonly Icon _iconLive, _iconRec, _iconPaused;
    FileSystemWatcher? _settingsWatcher;
    System.Threading.Timer? _reloadDebounce;
    bool _paused;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = Settings.Load();
        _iconLive = MakeIcon(Green); _iconRec = MakeIcon(Red); _iconPaused = MakeIcon(Grey);

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _saveItem = new ToolStripMenuItem("Save clip", null, (_, _) => SaveClip());
        _recordItem = new ToolStripMenuItem("Start recording", null, (_, _) => ToggleRecording());
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = Autostart.IsEnabled };
        _updateItem = new ToolStripMenuItem("Install update", null, (_, _) => InstallUpdate()) { Visible = false, Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            _updateItem,
            new ToolStripSeparator(),
            _saveItem, _recordItem, _pauseItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open library", null, (_, _) => OpenLibrary()),
            new ToolStripMenuItem("Open clips folder", null, (_, _) => OpenFolder(_settings.ClipsFolder)),
            new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()),
            _startupItem,
            new ToolStripMenuItem("Open log", null, (_, _) => Shell(Log.CurrentFile)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit Replay Recorder", null, (_, _) => Quit()),
        });

        _tray = new NotifyIcon { Icon = _iconPaused, Text = "Replay Recorder", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenLibrary();

        _engine = CreateEngine();
        RegisterHotkeys();
        WatchSettings();
        ListenForCommand(Program.SaveEventName, SaveClip);
        ListenForCommand(Program.RecordEventName, ToggleRecording);
        Log.Info($"Replay Recorder started (ffmpeg: {Ffmpeg.FfmpegPath ?? "NOT FOUND"})");
        Displays.LogAll();
        _engine.Start();
        _storageTimer = new System.Threading.Timer(_ => CheckStorage(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
        if (Updater.JustUpdatedFrom("recorder") is { } from)
            _tray.ShowBalloonTip(5000, "Replay Recorder updated", $"Now on version {Updater.Current} (was {from}).", ToolTipIcon.Info);
        // First check a minute after start (not while Windows is still booting), then every 6 hours.
        _updateTimer = new System.Threading.Timer(_ => _ui.Post(async _ => await CheckForUpdateAsync(), null), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
    }

    // ------------------------------------------------------------------ updates

    async Task CheckForUpdateAsync()
    {
        var u = await Updater.CheckAsync();
        if (u == null || u.Version == _update?.Version) return;
        _update = u;
        _updateItem.Text = $"Install update {u.Version}";
        _updateItem.Visible = true;
        Log.Info($"Update available: {u.Version} (running {Updater.Current})");
        _tray.ShowBalloonTip(6000, "Replay Recorder update", $"Version {u.Version} is ready. Right-click the tray icon > Install update.", ToolTipIcon.Info);
    }

    async void InstallUpdate()
    {
        if (_update is not { } u) return;
        if (_engine.IsRecording)
        {
            _tray.ShowBalloonTip(4000, "Replay Recorder", "Stop the full recording first, then install the update.", ToolTipIcon.Info);
            return;
        }
        _updateItem.Enabled = false;
        _updateItem.Text = "Downloading update…";
        try
        {
            var file = await Updater.DownloadAsync(u);
            _tray.ShowBalloonTip(3000, "Replay Recorder", "Installing the update. The recorder restarts by itself in a few seconds.", ToolTipIcon.Info);
            await Task.Delay(1500);
            Updater.Install(file, reopenLibrary: false);
            Quit();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            _tray.ShowBalloonTip(6000, "Update failed", ex.Message, ToolTipIcon.Warning);
            _updateItem.Text = $"Install update {u.Version}";
            _updateItem.Enabled = true;
        }
    }

    ReplayEngine CreateEngine()
    {
        var e = new ReplayEngine(_settings);
        e.StatusChanged += () => _ui.Post(_ => RefreshUi(), null);
        e.Problem += msg => _ui.Post(_ => _tray.ShowBalloonTip(5000, "Replay Recorder", msg, ToolTipIcon.Warning), null);
        return e;
    }

    void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        if (!_hotkeys.Register(_settings.SaveClipHotkey, SaveClip))
            _tray.ShowBalloonTip(5000, "Replay Recorder", $"Couldn't register the save-clip hotkey {_settings.SaveClipHotkey}. Change it in settings.", ToolTipIcon.Warning);
        _hotkeys.Register(_settings.ToggleRecordingHotkey, ToggleRecording);
        _saveItem.ShortcutKeyDisplayString = _settings.SaveClipHotkey;
        _recordItem.ShortcutKeyDisplayString = _settings.ToggleRecordingHotkey;
    }

    void RefreshUi()
    {
        bool running = _engine.Running && !_paused;
        _tray.Icon = !running ? _iconPaused : _engine.IsRecording ? _iconRec : _iconLive;
        var status = _paused ? "Paused" : _engine.IsRecording ? "● Full recording in progress" : _engine.StatusText;
        _statusItem.Text = status;
        var tip = $"Replay Recorder – {status}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        _saveItem.Enabled = running;
        _recordItem.Enabled = running;
        _recordItem.Text = _engine.IsRecording ? "Stop recording" : "Start recording";
        _pauseItem.Text = _paused ? "Resume" : "Pause";
    }

    // ------------------------------------------------------------------ actions

    async void SaveClip()
    {
        if (_paused || !_engine.Running)
        {
            Log.Warn($"Save ignored: {(_paused ? "paused" : "capture not running")}");
            return;
        }
        var game = GameDetector.Detect(_settings.GameNames);
        var screen = CaptureScreenBounds();
        try
        {
            var path = await _engine.SaveClipAsync(game);
            if (_settings.PlaySaveSound) { if (path != null) SaveSound.Saved(); else SaveSound.Failed(); }
            if (path != null) Notify("Clip saved", $"{game} · last {_settings.BufferSeconds}s", Green, screen);
            else Notify("Clip not saved", "See the log for details", Red, screen);
            CheckStorage();
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
            if (_settings.PlaySaveSound) SaveSound.Failed();
            Notify("Clip not saved", ex.Message, Red, screen);
        }
    }

    async void ToggleRecording()
    {
        if (_paused || !_engine.Running) return;
        var screen = CaptureScreenBounds();
        if (!_engine.IsRecording)
        {
            var game = GameDetector.Detect(_settings.GameNames);
            _engine.StartRecording(game);
            Notify("Recording started", $"{game} · press {_settings.ToggleRecordingHotkey} to stop", Red, screen);
        }
        else
        {
            Notify("Saving recording…", "", Grey, screen);
            var path = await _engine.StopRecordingAsync();
            if (_settings.PlaySaveSound) { if (path != null) SaveSound.Saved(); else SaveSound.Failed(); }
            if (path != null) Notify("Recording saved", Path.GetFileName(path), Green, screen);
            else Notify("Recording not saved", "See the log for details", Red, screen);
        }
        RefreshUi();
    }

    void TogglePause()
    {
        _paused = !_paused;
        if (_paused) _engine.Stop(); else _engine.Start();
        RefreshUi();
    }

    void ToggleStartup()
    {
        Autostart.Set(!Autostart.IsEnabled);
        _startupItem.Checked = Autostart.IsEnabled;
        _settings.StartWithWindows = Autostart.IsEnabled;
        SaveSettingsQuietly();
    }

    void OpenLibrary()
    {
        var lib = Path.Combine(AppContext.BaseDirectory, "ReplayRecorder.exe");
        if (File.Exists(lib)) Process.Start(new ProcessStartInfo(lib) { UseShellExecute = true });
        else OpenFolder(_settings.ClipsFolder); // library arrives in stage 2
    }

    void OpenSettings()
    {
        var lib = Path.Combine(AppContext.BaseDirectory, "ReplayRecorder.exe");
        if (File.Exists(lib))
        {
            Process.Start(new ProcessStartInfo(lib, "--settings") { UseShellExecute = true });
            return;
        }
        // No library next to us (dev setups): fall back to editing the JSON.
        _settings.Save();
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Paths.SettingsFile}\"") { UseShellExecute = true });
    }

    void Quit()
    {
        _tray.Visible = false;
        ExitThread();
    }

    // ------------------------------------------------------------------ settings reload

    void WatchSettings()
    {
        _settingsWatcher = new FileSystemWatcher(Paths.AppData, "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler h = (_, _) =>
        {
            if (_ignoreSettingsChange) return;
            _reloadDebounce?.Dispose();
            _reloadDebounce = new System.Threading.Timer(_ => _ui.Post(_ => ReloadSettings(), null), null, 700, Timeout.Infinite);
        };
        _settingsWatcher.Changed += h;
        _settingsWatcher.Created += h;
        _settingsWatcher.Renamed += (s, e) => h(s, e);
    }

    volatile bool _ignoreSettingsChange;

    void SaveSettingsQuietly()
    {
        _ignoreSettingsChange = true;
        try { _settings.Save(); } finally { Task.Delay(1500).ContinueWith(_ => _ignoreSettingsChange = false); }
    }

    void ReloadSettings()
    {
        if (_engine.IsRecording)
        {
            _tray.ShowBalloonTip(4000, "Replay Recorder", "Settings will apply after the current recording stops.", ToolTipIcon.Info);
            return;
        }
        Log.Info("Settings changed, restarting capture");
        _settings = Settings.Load();
        _engine.Dispose();
        _engine = CreateEngine();
        RegisterHotkeys();
        if (!_paused) _engine.Start();
        RefreshUi();
        _tray.ShowBalloonTip(2000, "Replay Recorder", "Settings applied.", ToolTipIcon.Info);
    }

    // ------------------------------------------------------------------ helpers

    bool _storageWarned;
    System.Threading.Timer? _storageTimer;

    /// <summary>Warns once (per session, until it drops below again) when the clips folder is over the limit.</summary>
    void CheckStorage()
    {
        double limit = _settings.ClipsFolderLimitGB;
        if (limit <= 0) return;
        var folder = _settings.ClipsFolder;
        Task.Run(() =>
        {
            double gb = Paths.FolderSize(folder) / 1e9;
            _ui.Post(_ =>
            {
                if (gb <= limit) { _storageWarned = false; return; }
                if (_storageWarned) return;
                _storageWarned = true;
                Log.Warn($"Clips folder is {gb:0.0} GB (limit {limit:0} GB)");
                _tray.ShowBalloonTip(8000, "Replay Recorder: clips folder is getting big",
                    $"Your clips use {gb:0.0} GB (warning limit {limit:0} GB). Open the library to delete old clips.", ToolTipIcon.Warning);
            }, null);
        });
    }

    void ListenForCommand(string eventName, Action action)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => _ui.Post(_ => action(), null), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    void Notify(string title, string detail, Color accent, Rectangle screen)
    {
        if (_settings.ShowNotifications) Toast.Show(title, detail, accent, screen);
    }

    Rectangle CaptureScreenBounds()
    {
        var name = _engine.Display?.DeviceName;
        return (Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name) ?? Screen.PrimaryScreen!).Bounds;
    }

    static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Shell(path);
    }

    static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    static Icon MakeIcon(Color dot)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(91, 140, 255)); // Replay Recorder blue, as in the app icon
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 12, 12, 180, 90); path.AddArc(19, 1, 12, 12, 270, 90);
            path.AddArc(19, 19, 12, 12, 0, 90); path.AddArc(1, 19, 12, 12, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var white = new SolidBrush(Color.White);
            g.FillPolygon(white, new[] { new PointF(10, 8), new PointF(10, 24), new PointF(23, 16) });
            using var ring = new SolidBrush(Color.FromArgb(24, 24, 28)); // dark ring keeps the dot readable on the blue
            g.FillEllipse(ring, 18, 18, 14, 14);
            using var d = new SolidBrush(dot);
            g.FillEllipse(d, 20, 20, 10, 10);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settingsWatcher?.Dispose();
            _hotkeys.Dispose();
            _engine.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
