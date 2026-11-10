using System.Windows;
using System.Windows.Shell;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library;

/// <summary>
/// Own title bar (WindowChrome in MainWindow.xaml): Windows still moves, snaps and resizes the window;
/// this only draws the three buttons and keeps a maximised window inside the screen.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Fullscreen: no drag strip or resize edges. The chrome itself stays on: taking it off and putting it back
    /// would put its message hook in front of SnapHook, and the snap menu would stop working.
    /// </summary>
    void ChromeForFullscreen(bool on)
    {
        if (WindowChrome.GetWindowChrome(this) is not { } chrome) return;
        chrome.CaptionHeight = on ? 0 : 58;
        chrome.ResizeBorderThickness = new Thickness(on ? 0 : 6);
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        FitToScreenWhenMaximized();
    }

    /// <summary>
    /// A maximised window with a resize frame hangs over the screen edges by the frame's width; pad it back in
    /// (not in fullscreen, which has no frame). Also swaps the maximise / restore glyph.
    /// </summary>
    void FitToScreenWhenMaximized()
    {
        bool max = WindowState == WindowState.Maximized && _fullscreen == null;
        Root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxBtn.Content = max ? "\uE923" : "\uE922";
        MaxBtn.ToolTip = (max ? "Restore" : "Maximize").T();
    }

    // ---- Windows 11 snap layouts: Windows shows them only over a spot reported as the maximise button
    // (HTMAXBUTTON). The mouse is then "outside" the WPF window, so the click and the hover look are done here.

    const int WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2,
              WM_NCLBUTTONDBLCLK = 0xA3, WM_MOUSEMOVE = 0x200, WM_NCMOUSELEAVE = 0x2A2, HTMAXBUTTON = 9;
    bool _maxPressed;

    IntPtr SnapHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        bool onMax = wParam.ToInt64() == HTMAXBUTTON;
        switch (msg)
        {
            case WM_NCHITTEST when OverMaxButton(lParam):
                handled = true;
                return (IntPtr)HTMAXBUTTON;
            case WM_NCMOUSEMOVE:
                SetMaxHover(onMax);
                if (onMax) TrackNonClientLeave(hwnd);
                break;
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when onMax:
                _maxPressed = handled = true; // not to Windows: it would draw an old-style button
                break;
            case WM_NCLBUTTONUP when onMax:
                if (_maxPressed) Maximize_Click(MaxBtn, new RoutedEventArgs());
                _maxPressed = false;
                handled = true;
                break;
            case WM_NCMOUSELEAVE or WM_MOUSEMOVE:
                SetMaxHover(false);
                _maxPressed = false;
                break;
        }
        return IntPtr.Zero;
    }

    bool OverMaxButton(IntPtr lParam)
    {
        if (_fullscreen != null || !MaxBtn.IsVisible) return false;
        long l = lParam.ToInt64();
        var p = MaxBtn.PointFromScreen(new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)));
        return p.X >= 0 && p.Y >= 0 && p.X < MaxBtn.ActualWidth && p.Y < MaxBtn.ActualHeight;
    }

    /// <summary>The look the button's own IsMouseOver trigger would give (CaptionBtn in MainWindow.xaml).</summary>
    void SetMaxHover(bool on)
    {
        if (on)
        {
            MaxBtn.SetResourceReference(BackgroundProperty, "Glass2");
            MaxBtn.SetResourceReference(ForegroundProperty, "Text");
        }
        else
        {
            MaxBtn.ClearValue(BackgroundProperty);
            MaxBtn.ClearValue(ForegroundProperty);
        }
    }

    /// <summary>Ask for WM_NCMOUSELEAVE, so the hover look goes away when the mouse leaves the window.</summary>
    static void TrackNonClientLeave(IntPtr hwnd)
    {
        var t = new TRACKMOUSEEVENT { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 | 0x10, hwndTrack = hwnd };
        TrackMouseEvent(ref t);
    }

    struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
}
