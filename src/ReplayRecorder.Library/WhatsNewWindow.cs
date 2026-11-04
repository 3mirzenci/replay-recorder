using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library;

/// <summary>
/// "What's new" pop-up shown once after an update: the GitHub release notes of the new version.
/// shortcut: renders only the Markdown release.ps1 notes use (lines, "- " bullets, "#" headings,
/// images as ![](url) or &lt;img src&gt;); use a Markdown library if notes need tables or nesting.
/// </summary>
static partial class WhatsNewWindow
{
    [GeneratedRegex("""!\[[^\]]*\]\((\S+?)\)|<img[^>]*?src="([^"]+)"[^>]*>""")] private static partial Regex ImageRx();
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")] private static partial Regex LinkRx();

    public static async Task ShowAsync(Window owner, Version version)
    {
        var notes = await Updater.NotesForAsync(version);
        if (string.IsNullOrWhiteSpace(notes)) return; // offline or no notes: the "Updated ✓" banner still shows
        notes = Lang.PickNotes(notes);

        var text = (Brush)owner.FindResource("Text");
        var body = new StackPanel();
        foreach (var raw in notes.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var img = ImageRx().Match(line);
            if (img.Success)
            {
                var url = img.Groups[1].Success ? img.Groups[1].Value : img.Groups[2].Value;
                if (!Updater.IsGitHub(url)) continue; // images only from GitHub, like the update itself
                var image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(0, 8, 0, 8) };
                try { image.Source = new BitmapImage(new Uri(url)); } catch { continue; }
                image.ImageFailed += (_, _) => image.Visibility = Visibility.Collapsed;
                body.Children.Add(image);
                line = ImageRx().Replace(line, "").Trim();
            }
            if (line.Length == 0) continue;

            line = LinkRx().Replace(line, "$1").Replace("**", "").Replace("`", "");
            var heading = line.StartsWith('#');
            if (heading) line = line.TrimStart('#').Trim();
            else if (line.StartsWith("- ") || line.StartsWith("* ")) line = "•  " + line[2..];
            body.Children.Add(new TextBlock
            {
                Text = line, Foreground = text, TextWrapping = TextWrapping.Wrap,
                FontSize = heading ? 15 : 13.5, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, heading ? 10 : 3, 0, 3),
            });
        }

        var ok = new Button { Content = "Got it".T(), Style = (Style)owner.FindResource("AccentBtn"), IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var win = new Window
        {
            Title = "What's new".T(),
            Owner = owner,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)owner.FindResource("Panel"),
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = "What's new in {0}".T(version), Foreground = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) },
                    new ScrollViewer { Content = body, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    ok,
                },
            },
        };
        win.SourceInitialized += (_, _) => App.UseDarkTitleBar(win);
        ok.Click += (_, _) => win.Close();
        win.Show();
    }
}
