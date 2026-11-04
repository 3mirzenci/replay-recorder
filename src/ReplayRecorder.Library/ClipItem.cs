using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library;

/// <summary>A clip card in the grid.</summary>
sealed class ClipItem : INotifyPropertyChanged
{
    public ClipInfo Clip { get; }
    public ClipItem(ClipInfo clip)
    {
        Clip = clip;
        _fav = ClipEdits.Get(clip.Path).Favorite;
    }

    /// <summary>Card width in the grid (the hover strip is laid out in multiples of it).</summary>
    public const double CardWidth = 296;

    bool _fav;
    public bool IsFavorite { get => _fav; set { if (_fav != value) { _fav = value; OnChanged(); } } }

    bool _inView;
    /// <summary>
    /// True while the card is on screen or close to it. Images are only decoded for those cards and
    /// dropped again when they scroll far away, so a library of thousands of clips stays light.
    /// </summary>
    public bool InView
    {
        get => _inView;
        set
        {
            if (_inView == value) return;
            _inView = value;
            if (!value) { _thumb = null; _storyboard = null; EndScrub(); }
            OnChanged(nameof(Thumb)); OnChanged(nameof(Storyboard));
        }
    }

    public string Path => Clip.Path;
    public string Title => Clip.Title;
    public string Game => Clip.Game;
    public DateTime Created => Clip.Created;
    public long SizeBytes => Clip.SizeBytes;
    public double Duration => Clip.Duration;

    public string DurationText => Clip.Duration > 0 ? Player.TrimTimeline.Fmt(Clip.Duration) : "";
    public string SizeText => FormatSize(Clip.SizeBytes);
    public string QualityText => Clip.Height > 0 ? $"{Clip.Height}p{Math.Round(Clip.Fps)}" : "";
    public string DateText
    {
        get
        {
            var d = Clip.Created;
            if (d.Date == DateTime.Today) return $"Today {d:HH:mm}";
            if (d.Date == DateTime.Today.AddDays(-1)) return $"Yesterday {d:HH:mm}";
            return d.Year == DateTime.Today.Year ? d.ToString("d MMM HH:mm") : d.ToString("d MMM yyyy");
        }
    }
    /// <summary>True while the grid is grouped by day: the day header already says the date, so cards show only the time.</summary>
    public static bool GroupedByDay { get; set; } = true;
    public DayGroup Day => DayGroup.For(Clip.Created);
    public string Subtitle => string.Join("  ·  ", new[] { GroupedByDay ? Clip.Created.ToString("HH:mm") : DateText, SizeText, QualityText }.Where(s => s.Length > 0));

    ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get
        {
            if (!_inView) return null;
            if (_thumb == null && Clip.ThumbPath != null) _thumb = LoadImage(Clip.ThumbPath, 400);
            return _thumb;
        }
    }

    ImageSource? _cover;
    /// <summary>Small thumbnail that ignores <see cref="InView"/>: for the header and the sidebar's game covers.</summary>
    public ImageSource? Cover => _cover ??= Clip.ThumbPath != null ? LoadImage(Clip.ThumbPath, 240) : null;

    internal static ImageSource? LoadImage(string path, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ---- hover scrub: a strip of frames across the clip, shifted to the frame under the mouse

    public string? StoryboardPath { get; set; }
    public bool StoryboardRequested { get; set; }

    ImageSource? _storyboard;
    public ImageSource? Storyboard => _inView ? _storyboard : null;

    public void SetStoryboard(string path)
    {
        _storyboard = LoadImage(path, ClipIndex.StoryboardFrames * 320);
        OnChanged(nameof(Storyboard));
    }

    double _scrubOffset, _scrubBar;
    bool _scrubbing;
    public double ScrubOffset { get => _scrubOffset; private set { _scrubOffset = value; OnChanged(); } }
    public double ScrubBarWidth { get => _scrubBar; private set { _scrubBar = value; OnChanged(); } }
    public bool IsScrubbing { get => _scrubbing; private set { if (_scrubbing != value) { _scrubbing = value; OnChanged(); } } }

    /// <summary>Shows the frame at <paramref name="fraction"/> (0..1) of the clip.</summary>
    public void Scrub(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 0.999);
        int frame = (int)(fraction * ClipIndex.StoryboardFrames);
        ScrubOffset = -frame * CardWidth;
        ScrubBarWidth = fraction * CardWidth;
        IsScrubbing = _storyboard != null;
    }

    public void EndScrub() { IsScrubbing = false; ScrubBarWidth = 0; }

    /// <summary>Call after probing / thumbnailing filled in new data.</summary>
    public void Refresh()
    {
        _thumb = null;
        OnChanged(nameof(Thumb)); OnChanged(nameof(Cover)); OnChanged(nameof(DurationText)); OnChanged(nameof(Subtitle));
        OnChanged(nameof(QualityText)); OnChanged(nameof(Duration));
    }

    public void RefreshSubtitle() => OnChanged(nameof(Subtitle));

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>A row in the game filter sidebar.</summary>
sealed record GameEntry(string Name, int Count, bool IsAll = false, bool IsFavorites = false, ImageSource? ClipCover = null, ImageSource? Icon = null)
{
    /// <summary>The game's exe icon (saved by the recorder) when there is one, else its newest clip.</summary>
    public ImageSource? Cover => Icon ?? ClipCover;

    public static ImageSource? LoadIcon(string game)
    {
        var file = Core.Paths.GameIconFile(game);
        return System.IO.File.Exists(file) ? ClipItem.LoadImage(file, 128) : null;
    }

    public string Label => IsAll ? "All clips" : IsFavorites ? "Favorites" : Name;
    public string Detail => Count == 1 ? "1 clip" : $"{Count} clips";
}

/// <summary>
/// One day in the grid. Used as the group key, so there is one shared instance per date;
/// the header binds to it and MainWindow updates the counts and selection state.
/// </summary>
sealed class DayGroup : INotifyPropertyChanged
{
    static readonly Dictionary<DateTime, DayGroup> Days = new();
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    public static DayGroup For(DateTime when)
    {
        var d = when.Date;
        if (!Days.TryGetValue(d, out var g)) Days[d] = g = new DayGroup(d);
        return g;
    }

    DayGroup(DateTime date) => Date = date;
    public DateTime Date { get; }

    public string Label =>
        Date == DateTime.Today ? "Today" :
        Date == DateTime.Today.AddDays(-1) ? "Yesterday" :
        Date > DateTime.Today.AddDays(-7) ? Date.ToString("dddd", En) :
        Date.Year == DateTime.Today.Year ? Date.ToString("dddd, d MMMM", En) : Date.ToString("d MMMM yyyy", En);

    string _summary = "";
    public string Summary { get => _summary; set { if (_summary != value) { _summary = value; OnChanged(); } } }

    /// <summary>0 = none of the day's clips selected, 1 = some, 2 = all.</summary>
    int _state;
    public int State { get => _state; set { if (_state != value) { _state = value; OnChanged(); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
