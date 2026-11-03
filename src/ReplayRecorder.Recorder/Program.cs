using ReplayRecorder.Core;

namespace ReplayRecorder.Recorder;

static class Program
{
    // Named events let a second launch ("ReplayRecorder.Recorder.exe --save") trigger the running
    // instance: handy for Stream Deck buttons, macros and testing.
    public const string SaveEventName = @"Local\ReplayRecorder.SaveClip";
    public const string RecordEventName = @"Local\ReplayRecorder.ToggleRecording";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.FirstOrDefault()?.ToLowerInvariant() == "--diag-audio")
        {
            Audio.AudioDiagnostics.Run();
            return;
        }

        using var single = new Mutex(true, @"Local\ReplayRecorder.Recorder", out bool first);
        if (!first)
        {
            var cmd = args.FirstOrDefault()?.ToLowerInvariant();
            var name = cmd switch { "--save" => SaveEventName, "--toggle-record" => RecordEventName, _ => null };
            if (name != null && EventWaitHandle.TryOpenExisting(name, out var ev)) { ev.Set(); ev.Dispose(); }
            return; // already running in the tray
        }

        Migration.Run(); // before logging: see Migration
        Log.Init("recorder");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        using var app = new TrayApp();
        Application.Run(app);
        Log.Info("Replay Recorder exited");
    }
}
