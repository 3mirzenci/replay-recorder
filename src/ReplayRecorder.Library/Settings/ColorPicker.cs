using ReplayRecorder.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ReplayRecorder.Library;

/// <summary>Small dark dialog for a custom accent colour: colour square, hue bar, colour code and a preview.</summary>
static class ColorPicker
{
    const double W = 352, H = 180;

    /// <summary>Returns "#RRGGBB", or null when cancelled.</summary>
    public static string? Ask(Window owner, Color start)
    {
        var (h, s, v) = ColorHsv.FromRgb(start.R, start.G, start.B);
        Color Current() { var (r, g, b) = ColorHsv.ToRgb(h, s, v); return Color.FromRgb(r, g, b); }
        Brush Res(string key) => (Brush)owner.FindResource(key);

        // Colour square: hue underneath, white fading in from the left, black from the bottom.
        var hueFill = new SolidColorBrush();
        Rectangle Layer(Brush fill) => new() { Width = W, Height = H, RadiusX = 6, RadiusY = 6, Fill = fill };
        var svMark = new Ellipse { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        var square = new Canvas { Width = W, Height = H, Background = Brushes.Transparent, Cursor = Cursors.Cross, Children =
        {
            Layer(hueFill),
            Layer(new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0)),
            Layer(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90)),
            svMark,
        } };

        var hueStops = new GradientStopCollection();
        for (int i = 0; i <= 6; i++) { var (r, g, b) = ColorHsv.ToRgb(i * 60, 1, 1); hueStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 6.0)); }
        var hueMark = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, IsHitTestVisible = false };
        var hueBar = new Canvas { Width = W, Height = 16, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 14, 0, 0), Children =
        {
            new Rectangle { Width = W, Height = 8, RadiusX = 4, RadiusY = 4, Fill = new LinearGradientBrush(hueStops, 0), Margin = new Thickness(0, 4, 0, 0) },
            hueMark,
        } };

        var code = new TextBox { Style = (Style)owner.FindResource("Field"), Width = 100, MaxLength = 7, FontFamily = (FontFamily)owner.FindResource("NumFont"), VerticalAlignment = VerticalAlignment.Center };
        var warn = new TextBlock { Text = "This colour may be hard to see on the dark background.".T(), Foreground = Res("Subtle"), FontSize = 12, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        // The "Use colour" button is drawn in the picked colour, so it doubles as the preview.
        var ok = new Button { Content = "Use colour".T(), Style = (Style)owner.FindResource("AccentBtn"), IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel".T(), Style = (Style)owner.FindResource("GhostBtn"), IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { cancel, ok } };
        DockPanel.SetDock(buttons, Dock.Right);
        var codeRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 18, 0, 0), Children =
        {
            buttons,
            new TextBlock { Text = "Colour code".T(), Foreground = Res("Subtle"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) },
            code,
        } };

        var bg = ((SolidColorBrush)Res("Bg")).Color;
        bool typing = false;
        void Show()
        {
            var c = Current();
            var (hr, hg, hb) = ColorHsv.ToRgb(h, 1, 1);
            hueFill.Color = Color.FromRgb(hr, hg, hb);
            Canvas.SetLeft(svMark, s * W - 7); Canvas.SetTop(svMark, (1 - v) * H - 7);
            Canvas.SetLeft(hueMark, h / 360 * W - 7); Canvas.SetTop(hueMark, 1);
            ok.Background = ok.BorderBrush = new SolidColorBrush(c);
            // Same rule the app uses for text on the accent.
            ok.Foreground = ColorContrast.DarkTextOn(c.R, c.G, c.B)
                ? new SolidColorBrush(Color.FromRgb(ColorContrast.DarkR, ColorContrast.DarkG, ColorContrast.DarkB)) : Brushes.White;
            // Accent marks (icons, selection) need 3:1 against the window background to be seen.
            warn.Visibility = ColorContrast.Ratio(ColorContrast.Luminance(c.R, c.G, c.B), ColorContrast.Luminance(bg.R, bg.G, bg.B)) < 3
                ? Visibility.Visible : Visibility.Collapsed;
            if (!typing) code.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        void Drag(FrameworkElement area, Action<Point> set)
        {
            area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); set(e.GetPosition(area)); Show(); };
            area.MouseMove += (_, e) => { if (area.IsMouseCaptured) { set(e.GetPosition(area)); Show(); } };
            area.MouseLeftButtonUp += (_, _) => area.ReleaseMouseCapture();
        }
        Drag(square, p => { s = Math.Clamp(p.X / W, 0, 1); v = 1 - Math.Clamp(p.Y / H, 0, 1); });
        Drag(hueBar, p => h = Math.Clamp(p.X / W, 0, 1) * 359.9);

        code.TextChanged += (_, _) =>
        {
            var t = code.Text.Trim().TrimStart('#');
            if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int n)) return;
            (h, s, v) = ColorHsv.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n);
            typing = true; Show(); typing = false;
        };

        var win = new Window
        {
            Title = "Custom colour".T(),
            Owner = owner,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        DialogFrame.Apply(win, new StackPanel { Children = { square, hueBar, codeRow, warn } });
        ok.Click += (_, _) => win.DialogResult = true;
        Show();
        win.Loaded += (_, _) => { code.Focus(); code.SelectAll(); };
        if (win.ShowDialog() != true) return null;
        var c = Current();
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
