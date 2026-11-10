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
    WindowChrome? _chrome;

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
}
