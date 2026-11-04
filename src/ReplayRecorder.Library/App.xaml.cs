using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library;

public partial class App : Application
{
    const string ShowEventName = @"Local\ReplayRecorder.Library.Show";
    const string SettingsEventName = @"Local\ReplayRecorder.Library.Settings";
    Mutex? _single;

    /// <summary>"--update-now": install an available update right after the check (no click needed).</summary>
    public bool UpdateNow { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        UpdateNow = e.Args.Contains("--update-now");
        bool wantSettings = e.Args.Contains("--settings");
        _single = new Mutex(true, @"Local\ReplayRecorder.Library", out bool first);
        if (!first)
        {
            // Already open: bring that window forward (or its settings) instead of starting a second copy.
            if (EventWaitHandle.TryOpenExisting(wantSettings ? SettingsEventName : ShowEventName, out var ev)) { ev.Set(); ev.Dispose(); }
            Shutdown();
            return;
        }

        Migration.Run(); // before logging: see Migration
        Log.Init("library");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("UI exception", ex.Exception);
            MessageBox.Show(ex.Exception.Message, "Replay Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        var saved = Settings.Load();
        ApplyTheme(saved.Theme, saved.AccentColor);
        StartRecorderIfNeeded();
        base.OnStartup(e);
        // "ReplayRecorder.exe --open <clip>" opens that clip straight in the player.
        int oi = Array.IndexOf(e.Args, "--open");
        var win = new MainWindow(oi >= 0 && oi + 1 < e.Args.Length ? e.Args[oi + 1] : null);
        MainWindow = win;
        win.Show();

        void Front()
        {
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
        }
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) => Dispatcher.BeginInvoke(Front), null, Timeout.Infinite, false);
        var settings = new EventWaitHandle(false, EventResetMode.AutoReset, SettingsEventName);
        ThreadPool.RegisterWaitForSingleObject(settings, (_, _) => Dispatcher.BeginInvoke(() => { Front(); win.OpenSettings(); }),
            null, Timeout.Infinite, false);
        if (wantSettings) win.Dispatcher.BeginInvoke(win.OpenSettings, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Opening the library also starts the tray recorder when it isn't running, so one shortcut is
    /// enough. The recorder's single-instance mutex tells whether it runs; a duplicate start just exits.
    /// </summary>
    static void StartRecorderIfNeeded()
    {
        if (Environment.GetEnvironmentVariable("REPLAYRECORDER_PROFILE") != null) return; // test runs
        if (Mutex.TryOpenExisting(@"Local\ReplayRecorder.Recorder", out var m)) { m.Dispose(); return; }
        var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "ReplayRecorder.Recorder.exe");
        if (!System.IO.File.Exists(exe)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
            Log.Info("Recorder wasn't running; started it");
        }
        catch (Exception ex) { Log.Error("Couldn't start the recorder", ex); }
    }

    /// <summary>Starts a fresh copy of the library and closes this one.</summary>
    public void Restart()
    {
        _single?.ReleaseMutex();
        _single?.Dispose();
        _single = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        Shutdown();
    }

    /// <summary>Swaps the colour/font dictionary; every DynamicResource updates live.</summary>
    public static void ApplyTheme(string theme, string accent = "")
    {
        var file = theme is "amber" or "booth" ? "Amber" : "Windows"; // "booth" = older name for amber
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{file}.xaml") };
        if (TryParseColor(accent, out var c))
        {
            dict["Accent"] = Frozen(c);
            dict["AccentSoft"] = Frozen(System.Windows.Media.Color.FromArgb(0x2E, c.R, c.G, c.B));
            // Dark text on light accents, white on dark ones (perceived brightness).
            bool light = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150;
            dict["OnAccent"] = Frozen(light ? System.Windows.Media.Color.FromRgb(0x10, 0x10, 0x12) : System.Windows.Media.Colors.White);
        }
        Current.Resources.MergedDictionaries[0] = dict;
        foreach (Window w in Current.Windows) UseDarkTitleBar(w);
    }

    static System.Windows.Media.SolidColorBrush Frozen(System.Windows.Media.Color c) { var b = new System.Windows.Media.SolidColorBrush(c); b.Freeze(); return b; }

    static bool TryParseColor(string s, out System.Windows.Media.Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        try { c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s); return true; }
        catch (Exception e) when (e is FormatException or NotSupportedException) { return false; } // hand-edited settings file: fall back to the look's colour
    }

    /// <summary>Gives a window the Windows 11 dark title bar.</summary>
    public static void UseDarkTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        var bg = ((System.Windows.Media.SolidColorBrush)Current.Resources["Bg"]).Color;
        int caption = bg.R | bg.G << 8 | bg.B << 16; // COLORREF is BGR
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
