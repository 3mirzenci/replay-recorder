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
        Lang.Use(saved.Language);
        // XAML text is English. Windows translate their tree when built (TranslateTree); tooltips and
        // context menus are translated as they open, which also covers ones inside templates.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent,
            new System.Windows.Controls.ToolTipEventHandler((s, _) => { if (s is FrameworkElement { ToolTip: string tip } fe) fe.ToolTip = Lang.Retranslate(tip); }));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ContextMenuOpeningEvent,
            new System.Windows.Controls.ContextMenuEventHandler((s, _) => { if (s is FrameworkElement { ContextMenu: { } m }) TranslateTree(m); }));
        KeepFocusOnMouseClicks();
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

    /// <summary>
    /// Buttons can take keyboard focus (Tab), but a mouse click hands focus back to where it was, as when
    /// buttons couldn't take focus at all: arrow keys keep moving through the clips after clicking a button.
    /// </summary>
    static void KeepFocusOnMouseClicks()
    {
        IInputElement? before = null;
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new System.Windows.Input.MouseButtonEventHandler((_, _) => before = System.Windows.Input.Keyboard.FocusedElement), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler((s, _) =>
            {
                var prev = before; before = null;
                if (System.Windows.Input.InputManager.Current.MostRecentInputDevice is not System.Windows.Input.MouseDevice) return;
                // Only buttons that took focus themselves: a dropdown's inner toggle can't, and moving focus would close the list.
                if (s is not UIElement { IsKeyboardFocused: true }) return;
                if (prev is not UIElement p || ReferenceEquals(p, s) || Window.GetWindow(p) != Window.GetWindow((DependencyObject)s)) return;
                Restore(p);
            }), true);
        // Dropdowns too: after picking with the mouse, arrow keys move through the clips, not the list's items.
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ComboBox), UIElement.PreviewMouseLeftButtonDownEvent,
            new System.Windows.Input.MouseButtonEventHandler((s, _) =>
            {
                var cb = (System.Windows.Controls.ComboBox)s;
                if (cb.IsDropDownOpen || System.Windows.Input.Keyboard.FocusedElement is not UIElement p || cb.IsAncestorOf(p) || ReferenceEquals(p, cb)
                    || Window.GetWindow(p) != Window.GetWindow(cb)) return;
                EventHandler? closed = null;
                closed = (_, _) => { cb.DropDownClosed -= closed; if (cb.IsKeyboardFocusWithin) Restore(p); };
                cb.DropDownClosed += closed;
            }), true);

        // After the click's own handlers ran; skipped if the click closed or covered that window.
        static void Restore(UIElement p) =>
            p.Dispatcher.BeginInvoke(() => { if (p.IsVisible && Window.GetWindow(p)?.IsActive == true) p.Focus(); },
                System.Windows.Threading.DispatcherPriority.Input);
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
    /// <summary>Puts every literal text in this tree (logical and visual) into the current language.</summary>
    public static void TranslateTree(DependencyObject root) => TranslateTree(root, new HashSet<DependencyObject>());

    static void TranslateTree(DependencyObject o, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(o)) return;
        if (o is FrameworkElement fe)
        {
            Translate(fe);
            if (fe.ContextMenu != null) TranslateTree(fe.ContextMenu, seen);
        }
        foreach (var child in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) TranslateTree(child, seen);
        if (o is System.Windows.Media.Visual)
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++)
                TranslateTree(System.Windows.Media.VisualTreeHelper.GetChild(o, i), seen);
    }

    static void Translate(FrameworkElement e)
    {
        static bool Bound(DependencyObject o, DependencyProperty p) => System.Windows.Data.BindingOperations.IsDataBound(o, p);
        if (e is Window w) w.Title = Lang.Retranslate(w.Title);
        else if (e is System.Windows.Controls.TextBlock t && !Bound(t, System.Windows.Controls.TextBlock.TextProperty)) t.Text = Lang.Retranslate(t.Text);
        else if (e is System.Windows.Controls.HeaderedItemsControl h && h.Header is string hs) h.Header = Lang.Retranslate(hs);
        else if (e is System.Windows.Controls.ContentControl c && c.Content is string cs && !Bound(c, System.Windows.Controls.ContentControl.ContentProperty)) c.Content = Lang.Retranslate(cs);
        if (e.ToolTip is string tip) e.ToolTip = Lang.Retranslate(tip);
    }

    public static void ApplyTheme(string theme, string accent = "")
    {
        // "windows"/"amber" = the two Classic looks ("booth" = older name for amber); anything else is Aurora.
        var file = theme switch { "windows" => "Windows", "amber" or "booth" => "Amber", _ => "Aurora" };
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{file}.xaml") };
        if (TryParseColor(accent, out var c))
        {
            dict["Accent"] = Frozen(c);
            dict["AccentSoft"] = Frozen(System.Windows.Media.Color.FromArgb(0x2E, c.R, c.G, c.B));
            // Dark or white text on the accent, whichever has more contrast.
            dict["OnAccent"] = Frozen(ColorContrast.DarkTextOn(c.R, c.G, c.B)
                ? System.Windows.Media.Color.FromRgb(ColorContrast.DarkR, ColorContrast.DarkG, ColorContrast.DarkB)
                : System.Windows.Media.Colors.White);
        }
        else c = ((System.Windows.Media.SolidColorBrush)dict["Accent"]).Color; // the look's own accent and its own text colour
        // Faint accent light: along the top edge (glow) and around the video (halo); 0 in Classic.
        dict["AccentGlow"] = Light(c, (double)dict["GlowOpacity"], new(0.5, 0), 0.95, 0.6);
        dict["AccentHalo"] = Light(c, (double)dict["HaloOpacity"], new(0.5, 0.5), 0.5, 0.5);
        Current.Resources.MergedDictionaries[0] = dict;
        foreach (Window w in Current.Windows) UseDarkTitleBar(w);
    }

    static System.Windows.Media.SolidColorBrush Frozen(System.Windows.Media.Color c) { var b = new System.Windows.Media.SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>The accent fading from <paramref name="opacity"/> at <paramref name="center"/> to nothing at the rim.</summary>
    static System.Windows.Media.RadialGradientBrush Light(System.Windows.Media.Color c, double opacity, Point center, double rx, double ry)
    {
        var b = new System.Windows.Media.RadialGradientBrush(
            System.Windows.Media.Color.FromArgb((byte)Math.Round(255 * opacity), c.R, c.G, c.B),
            System.Windows.Media.Color.FromArgb(0, c.R, c.G, c.B))
        { Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry };
        b.Freeze();
        return b;
    }

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
