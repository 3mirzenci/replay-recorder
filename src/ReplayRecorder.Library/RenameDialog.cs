using ReplayRecorder.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReplayRecorder.Library;

/// <summary>Small dark dialog asking for a new clip name (without extension).</summary>
static class RenameDialog
{
    public static string? Ask(Window owner, string current)
    {
        var box = new TextBox { Text = current, Style = (Style)owner.FindResource("Field"), Margin = new Thickness(0, 8, 0, 0) };
        var error = new TextBlock { Foreground = (Brush)owner.FindResource("Danger"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = "Rename".T(), Style = (Style)owner.FindResource("AccentBtn"), IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var cancel = new Button { Content = "Cancel".T(), Style = (Style)owner.FindResource("GhostBtn"), IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Children = { cancel, ok } };

        var win = new Window
        {
            Title = "Rename clip".T(),
            Owner = owner,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        DialogFrame.Apply(win, new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "New name".T(), Foreground = (Brush)owner.FindResource("Subtle"), FontSize = 12.5 },
                box, error, buttons,
            },
        });
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        ok.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (name.Length == 0) { error.Text = "Enter a name.".T(); return; }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { error.Text = "A name can't contain \\ / : * ? \" < > |".T(); return; }
            if (name.EndsWith('.')) { error.Text = "A name can't end with a dot.".T(); return; }
            win.DialogResult = true;
        };
        if (win.ShowDialog() != true) return null;
        var result = box.Text.Trim();
        return result.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
    }
}
