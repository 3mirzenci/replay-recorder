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

    // ---- grid layout, shared by every card: the grid fills the window's width (MainWindow.FitGrid)

    /// <summary>Card width; the hover strip is laid out in multiples of it.</summary>
    public static double TileWidth { get; private set; } = 296;
    public static double ThumbHeight => Math.Round(TileWidth * 9 / 16);
    public static double StripWidth => TileWidth * ClipIndex.StoryboardFrames;
    /// <summary>Rounded-corner clip for the thumbnail, its scrub strip and its bar.</summary>
    public static Geometry ThumbClip { get; private set; } = Geometry.Empty;
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    public static void SetTileWidth(double width, double radius)
    {
        TileWidth = width;
        var clip = new RectangleGeometry(new System.Windows.Rect(0, 0, width, ThumbHeight), radius, radius);
        clip.Freeze();
        ThumbClip = clip;
        foreach (var n in new[] { nameof(TileWidth), nameof(ThumbHeight), nameof(StripWidth), nameof(ThumbClip) })
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(n));
    }

    bool _new;
    /// <summary>The newest clip of today: gets the NEW badge.</summary>
    public bool IsNew { get => _new; set { if (_new != value) { _new = value; OnChanged(); } } }
    public static string NewLabel => "NEW".T();

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
            if (d.Date == DateTime.Today) return "Today {0:HH:mm}".T(d);
            if (d.Date == DateTime.Today.AddDays(-1)) return "Yesterday {0:HH:mm}".T(d);
            return d.Year == DateTime.Today.Year ? d.ToString("d MMM HH:mm", Lang.Culture) : d.ToString("d MMM yyyy", Lang.Culture);
        }
    }
    /// <summary>True while the grid is grouped by day: the day header already says the date, so cards show only the time.</summary>
    public static bool GroupedByDay { get; set; } = true;
    public DayGroup Day => DayGroup.For(Clip.Created);
    // Card label: the game (or the clip's own name once renamed), then the time; size and quality show on hover.
    static readonly System.Text.RegularExpressions.Regex DefaultName =
        new(@"\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}( \(recording\))?( \(\d+\))?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    public string DisplayName => DefaultName.IsMatch(Title) ? Game : Title;
    public string TimeText => GroupedByDay ? Clip.Created.ToString("HH:mm") : DateText;
    public string Detail => string.Join(" · ", new[] { SizeText, QualityText }.Where(s => s.Length > 0));
    public string Subtitle => string.Join(" · ", new[] { GroupedByDay ? Clip.Created.ToString("HH:mm") : DateText, SizeText, QualityText }.Where(s => s.Length > 0));

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

    static ImageSource? LoadImage(string path, int decodeWidth)
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
        ScrubOffset = -frame * TileWidth;
        ScrubBarWidth = fraction * TileWidth;
        IsScrubbing = _storyboard != null;
    }

    public void EndScrub() { IsScrubbing = false; ScrubBarWidth = 0; }

    /// <summary>Call after probing / thumbnailing filled in new data.</summary>
    public void Refresh()
    {
        _thumb = null;
        OnChanged(nameof(Thumb)); OnChanged(nameof(DurationText)); OnChanged(nameof(Subtitle));
        OnChanged(nameof(QualityText)); OnChanged(nameof(Duration)); OnChanged(nameof(Detail));
    }

    public void RefreshSubtitle() { OnChanged(nameof(Subtitle)); OnChanged(nameof(TimeText)); }

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
sealed record GameEntry(string Name, int Count, bool IsAll = false, bool IsFavorites = false)
{
    public string Label => IsAll ? "All clips".T() : IsFavorites ? "Favorites".T() : Name;
    public string Detail => (Count == 1 ? "1 clip" : "{0} clips").T(Count);
}

/// <summary>
/// One day in the grid. Used as the group key, so there is one shared instance per date;
/// the header binds to it and MainWindow updates the counts and selection state.
/// </summary>
sealed class DayGroup : INotifyPropertyChanged
{
    static readonly Dictionary<DateTime, DayGroup> Days = new();
    static CultureInfo En => Lang.Culture;

    public static DayGroup For(DateTime when)
    {
        var d = when.Date;
        if (!Days.TryGetValue(d, out var g)) Days[d] = g = new DayGroup(d);
        return g;
    }

    DayGroup(DateTime date) => Date = date;
    public void RefreshLabel() => OnChanged(nameof(Label));
    public DateTime Date { get; }

    public string Label =>
        Date == DateTime.Today ? "Today".T() :
        Date == DateTime.Today.AddDays(-1) ? "Yesterday".T() :
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
