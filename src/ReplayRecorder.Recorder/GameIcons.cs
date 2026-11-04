using System.Drawing;
using System.Drawing.Imaging;
using ReplayRecorder.Core;

namespace ReplayRecorder.Recorder;

/// <summary>
/// Saves each game's exe icon once (largest size, up to 256 px) so the library can show it as the
/// game's cover. Clips don't record where the game lives, so this is the only moment the path is known.
/// </summary>
static class GameIcons
{
    public static void Save(string game, string? exePath)
    {
        if (exePath == null) return;
        var file = Paths.GameIconFile(game);
        if (File.Exists(file)) return;
        try
        {
            using var icon = Icon.ExtractIcon(exePath, 0, 256);
            if (icon == null) return; // exe has no icon
            Directory.CreateDirectory(Paths.GameIconFolder);
            using var bmp = icon.ToBitmap();
            bmp.Save(file, ImageFormat.Png);
            Log.Info($"Saved game icon for {game}");
        }
        catch (Exception ex) { Log.Warn($"Game icon not saved for {game}: {ex.Message}"); }
    }
}
