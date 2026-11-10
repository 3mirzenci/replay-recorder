using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ReplayRecorder.Core;
using ReplayRecorder.Library.SettingsUi;

namespace ReplayRecorder.Library;

/// <summary>
/// First launch only: save hotkey, monitor and mic (with a live level bar), then Settings.SetupDone.
/// Everything here is also in Settings; Skip keeps the defaults.
/// </summary>
static class SetupWindow
{
    public static void Show(Window owner)
    {
        var s = Settings.Load();
        var text = (Brush)owner.FindResource("Text");
        var subtle = (Brush)owner.FindResource("Subtle");
        TextBlock Step(string t) => new() { Text = t, Foreground = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
        TextBlock Hint(string t) => new() { Text = t, Foreground = subtle, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

        // 1. Hotkey
        var hotkey = new HotkeyBox { Value = s.SaveClipHotkey };

        // 2. Monitor (automatic = primary, as in Settings)
        var displays = Displays.Enumerate();
        var monitors = new List<DeviceChoice> { new("", "Automatic: primary monitor".T()) };
        monitors.AddRange(displays.Select(d => new DeviceChoice(d.DeviceName, d.Label)));
        var monitor = new ComboBox { ItemsSource = monitors, SelectedIndex = Math.Max(0, monitors.FindIndex(c => c.Id.Length > 0 && c.Id == s.Monitor)) };

        // 3. Mic, with a live level bar so it's clear it works before anything is recorded
        var mics = AudioDevices.Mics();
        var mic = new ComboBox { ItemsSource = mics, SelectedIndex = Math.Max(0, mics.FindIndex(m => m.Id == s.MicDeviceId)) };
        var bar = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)) };
        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = (Brush)owner.FindResource("Line"), ClipToBounds = true, Margin = new Thickness(0, 8, 0, 0), Child = bar };
        var meter = new LevelMeter();
        void Listen() { var id = (mic.SelectedItem as DeviceChoice)?.Id ?? ""; Task.Run(() => meter.Start(id, loopback: false)); }
        mic.SelectionChanged += (_, _) => Listen();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => bar.Width = Math.Clamp((meter.ReadDb() + 60) / 60, 0, 1) * track.ActualWidth;

        var skip = new Button { Content = "Skip".T(), Style = (Style)owner.FindResource("GhostBtn"), IsCancel = true, MinWidth = 90 };
        var done = new Button { Content = "Done".T(), Style = (Style)owner.FindResource("AccentBtn"), IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var win = new Window
        {
            Title = "Replay Recorder",
            Owner = owner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)owner.FindResource("Panel"),
            Content = new StackPanel
            {
                Margin = new Thickness(24, 20, 24, 20),
                Children =
                {
                    new TextBlock { Text = "Welcome to Replay Recorder".T(), Foreground = text, FontSize = 20, FontWeight = FontWeights.SemiBold },
                    Hint("It's already recording in the background. Three quick checks, all changeable later in Settings.".T()),
                    Step("1. Save-clip hotkey".T()), hotkey,
                    Hint("Click the box and press the keys you want. Avoid Alt+F10 / Alt+F9 / Alt+Z: the NVIDIA overlay uses them.".T()),
                    Step("2. Screen to record".T()), monitor,
                    Step("3. Microphone".T()), mic, track,
                    Hint("Say something: the bar should move. If it doesn't, pick another mic.".T()),
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Children = { skip, done } },
                },
            },
        };
        win.SourceInitialized += (_, _) => App.UseDarkTitleBar(win);
        win.Loaded += (_, _) => { Listen(); timer.Start(); };
        win.Closed += (_, _) =>
        {
            timer.Stop();
            Task.Run(meter.Dispose);
            // Done or Skip: never shown again. Re-read so nothing the recorder saved meanwhile is lost.
            var now = Settings.Load();
            now.SetupDone = true;
            if (win.DialogResult == true)
            {
                if (!string.IsNullOrEmpty(hotkey.Value)) now.SaveClipHotkey = hotkey.Value;
                now.Monitor = (monitor.SelectedItem as DeviceChoice)?.Id ?? "";
                now.MicDeviceId = (mic.SelectedItem as DeviceChoice)?.Id ?? "";
            }
            now.Save();
            Log.Info($"First-run setup {(win.DialogResult == true ? "done" : "skipped")}");
        };
        done.Click += (_, _) => win.DialogResult = true;
        win.ShowDialog();
    }
}
