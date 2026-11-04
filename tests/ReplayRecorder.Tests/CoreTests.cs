using ReplayRecorder.Core;

public class ExportSizeTests
{
    static ClipInfo Clip(int w, int h, double fps) =>
        new() { Path = "x.mp4", Game = "g", Created = DateTime.Now, Width = w, Height = h, Fps = fps };

    [Theory]
    [InlineData(10_000_000, 30)]
    [InlineData(10_000_000, 60)]
    [InlineData(50_000_000, 120)]
    [InlineData(10_000_000, 300)]
    public void Budget_stays_under_the_target(long target, double seconds)
    {
        var (video, audio) = DiscordExporter.Budget(target, seconds);
        Assert.True(video > 0);
        Assert.True((video + audio) * 1000.0 / 8 * seconds <= target, "planned bitrate would overshoot the size limit");
    }

    [Fact]
    public void Plan_never_upscales_or_raises_fps()
    {
        var plan = DiscordExporter.PlanFor(Clip(1280, 720, 30), 20000, 128, "h264_nvenc");
        Assert.Equal((1280, 720, 30), (plan.Width, plan.Height, plan.Fps));
    }

    [Fact]
    public void Low_bitrate_picks_a_smaller_size()
    {
        var plan = DiscordExporter.PlanFor(Clip(2560, 1440, 60), 2000, 128, "h264_nvenc");
        Assert.Equal(720, plan.Height);
        Assert.Equal(30, plan.Fps);
        Assert.Equal(0, plan.Width % 2);
    }
}

public class SettingsTests
{
    [Fact]
    public void Volume_change_applies_without_restarting_capture()
    {
        var a = new Settings();
        var b = new Settings { SystemVolumeDb = 5 };
        Assert.False(a.CaptureDiffers(b));
    }

    [Fact]
    public void Fps_change_restarts_capture()
    {
        Assert.True(new Settings().CaptureDiffers(new Settings { Fps = 30 }));
    }

    [Theory]
    [InlineData("Call of Duty®: MW4", "Call of Duty®_ MW4")]
    [InlineData("  ..  ", "Unknown")]
    [InlineData(@"a/b\c", "a_b_c")]
    public void Clip_folder_names_are_safe(string game, string expected)
    {
        Assert.Equal(expected, Paths.SafeName(game));
    }
}
