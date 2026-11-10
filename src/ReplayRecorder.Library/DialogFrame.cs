using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library;

/// <summary>
/// The frame the small dialogs share (setup, rename, What's new): the app's own slim title bar with the
/// window's title and a close button, the window background and the faint accent light, as in the main window.
/// Closing with the X is the same as Esc / Cancel.
/// </summary>
static class DialogFrame
{
    const double BarHeight = 44;

    public static void Apply(Window win, FrameworkElement content)
    {
        win.SetResourceReference(Control.BackgroundProperty, "Bg");
        WindowChrome.SetWindowChrome(win, new WindowChrome
        {
            CaptionHeight = BarHeight, ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });

        var glow = new Rectangle { IsHitTestVisible = false };
        glow.SetResourceReference(Shape.FillProperty, "AccentGlow");
        var title = new TextBlock { Text = win.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        title.SetResourceReference(TextBlock.ForegroundProperty, "Subtle");
        var close = new Button { Content = "", ToolTip = "Close".T(), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0) };
        close.SetResourceReference(FrameworkElement.StyleProperty, "CloseBtn");
        close.Click += (_, _) => win.Close();
        var bar = new Grid { Height = BarHeight, VerticalAlignment = VerticalAlignment.Top, Children = { title, close } };

        content.Margin = new Thickness(24, BarHeight + 2, 24, 22);
        win.Content = new Grid { Children = { glow, content, bar } };
    }
}
