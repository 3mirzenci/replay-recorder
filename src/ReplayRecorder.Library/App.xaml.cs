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

    /// <summary>Starts a fresh copy of the library and closes this one.</summary>
    public void Restart()
    {
        _single?.ReleaseMutex();
        _single?.Dispose();
        _single = null;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        Shutdown();
    }

    /// <summary>Gives a window the Windows 11 dark title bar.</summary>
    public static void UseDarkTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        int caption = 0x00110E0D; // COLORREF (BGR) of #0D0E11
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
