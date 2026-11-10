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

    // The export line in the player: the same plan the export starts with.
    [Theory]
    [InlineData(60, "copy")]       // whole small clip: cut as is
    [InlineData(20, "copy")]
    [InlineData(600, "")]          // 10 min of a big clip: re-encoded
    public void Predict_matches_the_exporters_first_choice(double keptSeconds, string expected)
    {
        var clip = new ClipInfo { Path = "x.mp4", Game = "g", Created = DateTime.Now, Width = 1920, Height = 1080, Fps = 60,
                                  Duration = 600, SizeBytes = keptSeconds == 600 ? 3_000_000_000 : 10_000_000 };
        var plan = DiscordExporter.Predict(clip, new ClipEdit { Out = keptSeconds }, keptSeconds, 19_500_000);
        Assert.NotNull(plan);
        Assert.Equal(expected == "copy", plan!.StreamCopy);
        if (!plan.StreamCopy)
        {
            var (v, a) = DiscordExporter.Budget(19_500_000, keptSeconds);
            Assert.Equal(DiscordExporter.PlanFor(clip, v, a, "h264_nvenc"), plan);
            Assert.True(DiscordExporter.PredictBytes(clip, plan, keptSeconds) <= 19_500_000);
        }
    }

    [Fact]
    public void Predict_says_no_when_far_too_long()
    {
        var clip = new ClipInfo { Path = "x.mp4", Game = "g", Created = DateTime.Now, Width = 1920, Height = 1080, Fps = 60,
                                  Duration = 7200, SizeBytes = 30_000_000_000 };
        Assert.Null(DiscordExporter.Predict(clip, new ClipEdit(), 7200, 10_000_000));
    }

    [Fact]
    public void Volume_changes_rule_out_the_plain_cut()
    {
        var clip = new ClipInfo { Path = "x.mp4", Game = "g", Created = DateTime.Now, Width = 1920, Height = 1080, Fps = 60,
                                  Duration = 60, SizeBytes = 5_000_000 };
        Assert.False(DiscordExporter.Predict(clip, new ClipEdit { MicVolumeDb = 3 }, 60, 19_500_000)!.StreamCopy);
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

public class LangTests
{
    [Fact]
    public void Every_translation_keeps_the_same_placeholders()
    {
        var holes = new System.Text.RegularExpressions.Regex(@"\{\d+(:[^}]*)?\}");
        foreach (var (en, tr) in Lang.Entries)
        {
            var a = holes.Matches(en).Select(m => m.Value).Order();
            var b = holes.Matches(tr).Select(m => m.Value).Order();
            Assert.True(a.SequenceEqual(b), $"placeholders differ: \"{en}\" -> \"{tr}\"");
        }
    }

    [Fact]
    public void Switching_back_to_English_restores_every_plain_text()
    {
        Lang.Use("en");
        foreach (var (en, tr) in Lang.Entries.Where(kv => !kv.Key.Contains('{')))
            Assert.Equal(en, Lang.Retranslate(tr)); // fails if two English texts share one Turkish text
        Lang.Use("tr");
        Assert.Equal("Tüm klipler", Lang.Retranslate("All clips"));
        Lang.Use("en");
    }

    [Fact]
    public void Release_notes_show_in_the_app_language()
    {
        var notes = "- Fixed X\n\n<!-- tr -->\n- X düzeltildi";
        Lang.Use("en");
        Assert.Equal("- Fixed X", Lang.PickNotes(notes));
        Assert.Equal("- only English", Lang.PickNotes("- only English"));
        Lang.Use("tr");
        Assert.Equal("- X düzeltildi", Lang.PickNotes(notes));
        Assert.Equal("- only English", Lang.PickNotes("- only English")); // older notes without a Turkish part
        Lang.Use("en");
    }
}

public class CaptureRateTests
{
    [Theory]
    [InlineData(144, 60, 72)]
    [InlineData(180, 60, 90)]
    [InlineData(240, 60, 80)]
    [InlineData(165, 60, 82.5)]
    [InlineData(60, 60, 60)]
    [InlineData(120, 60, 120)]
    [InlineData(144, 30, 36)]
    public void Delivered_rate_stays_above_fps_with_fewer_frames(int refresh, int fps, double expectedDelivered)
    {
        double limit = Displays.CaptureRateLimit(refresh, fps);
        // What WGC does with the limit: wait 1/limit, then deliver on the next refresh tick.
        double tick = 1.0 / refresh;
        double delivered = 1.0 / (Math.Ceiling((1.0 / limit) / tick - 1e-9) * tick);
        Assert.Equal(expectedDelivered, delivered, 1);
        Assert.True(delivered >= fps, "would need doubled frames");
        Assert.True(limit <= 1000, "ffmpeg's max_framerate tops out at 1000");
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

public class SoundLevelTests
{
    [Fact]
    public void LevelsPerChannelPerBucket()
    {
        // Left: a full-scale square wave (0 dBFS RMS); right: half amplitude (about -6 dB). 2.5 buckets of 4 frames.
        var acc = new ReplayRecorder.Core.ClipIndex.LevelAccumulator(2, 4);
        var s = Enumerable.Range(0, 10).SelectMany(i => new[] { i % 2 == 0 ? 1f : -1f, 0.5f }).ToArray();
        acc.Add(s.AsSpan(0, 7)); // split mid-frame, as reads from a pipe are
        acc.Add(s.AsSpan(7));
        var lanes = acc.Finish();
        Assert.Equal(3, lanes[0].Length); // the last half bucket counts too
        Assert.All(lanes[0], v => Assert.Equal(0, v, 3));
        Assert.All(lanes[1], v => Assert.Equal(-6.02, v, 2));
    }

    [Fact]
    public void SilenceIsVeryQuietNotInfinite()
    {
        var acc = new ReplayRecorder.Core.ClipIndex.LevelAccumulator(1, 4);
        acc.Add(new float[4]);
        Assert.Equal(-100, acc.Finish()[0][0], 3);
    }
}

public class AutoDeleteTests
{
    static ReplayRecorder.Core.ClipInfo Clip(string name, DateTime created) =>
        new() { Path = name, Game = "CS2", Created = created };

    [Fact]
    public void OnlyOldClipsThatAreNotFavoritesAreDue()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        const string Old = @"C:\Clips\CS2\CS2 2026-10-01 20-00-00.mp4", Star = @"C:\Clips\CS2\CS2 2026-08-01 20-00-00.mp4";
        var clips = new[]
        {
            Clip(@"C:\Clips\CS2\CS2 2026-11-08 20-00-00.mp4", now.AddDays(-2)), Clip(Old, now.AddDays(-31)), Clip(Star, now.AddDays(-90)),
            Clip(@"C:\Clips\CS2\CS2 2026-10-11 14-00-00.mp4", now.AddDays(-29.9)),
            Clip(@"C:\Clips\holiday.mp4", now.AddDays(-400)),      // not made by the recorder
            Clip(@"C:\Clips\CS2\my ace.mp4", now.AddDays(-400)),   // renamed by the user
        };
        var due = ReplayRecorder.Core.AutoDelete.Due(clips, 30, p => p == Star, now);
        Assert.Equal(new[] { Old }, due.Select(c => c.Path));
    }
}

public class TrackLayoutTests
{
    static ClipInfo Clip(int tracks) => new() { Path = "x.mp4", Game = "g", Created = DateTime.Now, AudioTracks = tracks };

    [Fact]
    public void OldAndNewLayoutsAreDetected()
    {
        Assert.Equal((1, 2, (int?)3), Clip(4).SeparateTracks);
        Assert.Equal((1, 2, (int?)null), Clip(3).SeparateTracks);
        Assert.Equal((0, 1, (int?)null), Clip(2).SeparateTracks);
        Assert.Null(Clip(1).SeparateTracks);
    }

    [Fact]
    public void MixGraphAddsDiscordOnlyWhenPresent()
    {
        var four = DiscordExporter.MixGraph("0:a:", (1, 2, 3), 0, 0, -6, "[aout]");
        Assert.Contains("[0:a:3]volume=-6dB[da]", four);
        Assert.Contains("amix=inputs=3", four);
        var mpv = DiscordExporter.MixGraph("aid", (1, 2, null), 0, 0, 0, "[ao]");
        Assert.Contains("[aid2]", mpv);
        Assert.Contains("[aid3]", mpv);
        Assert.Contains("amix=inputs=2", mpv);
    }
}

public class AccentTextTests
{
    // Plan's accent test set: dark text on everything except the dark blue.
    [Theory]
    [InlineData("FFC21A", true)]
    [InlineData("FF4545", true)]
    [InlineData("3EDC6E", true)]
    [InlineData("3FC8FF", true)]
    [InlineData("FF5C8A", true)]
    [InlineData("9B6BFF", true)]
    [InlineData("EDEDED", true)]
    [InlineData("3B5BFF", false)]
    public void Picks_the_text_colour_with_more_contrast(string hex, bool dark)
    {
        byte C(int i) => Convert.ToByte(hex.Substring(i, 2), 16);
        Assert.Equal(dark, ColorContrast.DarkTextOn(C(0), C(2), C(4)));
    }
}
