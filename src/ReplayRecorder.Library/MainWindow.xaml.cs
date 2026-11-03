using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ReplayRecorder.Core;
using ReplayRecorder.Library.Player;

namespace ReplayRecorder.Library;

public partial class MainWindow : Window
{
    Settings _settings = Settings.Load();
    readonly ClipIndex _index;
    readonly ObservableCollection<ClipItem> _items = new();
    readonly ICollectionView _view;
    readonly CancellationTokenSource _cts = new();
    FileSystemWatcher? _watcher;
    readonly DispatcherTimer _rescanDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    string _gameFilter = ""; // "" = all clips, FavKey = favorites, else a game name
    const string FavKey = "\u0001favorites";
    string _search = "";

    // Player state
    MpvPlayer? _mpv;
    ClipItem? _current;
    ClipEdit _edit = new();
    bool _loadingClip;
    readonly DispatcherTimer _mixDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };

    readonly string? _openOnStart;

    public MainWindow(string? openOnStart = null)
    {
        _openOnStart = openOnStart;
        InitializeComponent();
        _index = new ClipIndex(_settings.ClipsFolder);
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = o => o is ClipItem c
            && (_gameFilter.Length == 0 || (_gameFilter == FavKey ? c.IsFavorite : c.Game == _gameFilter))
            && (_search.Length == 0 || c.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)
                                    || c.Game.Contains(_search, StringComparison.OrdinalIgnoreCase));
        ApplySort("newest");
        ClipGrid.ItemsSource = _view;

        _rescanDebounce.Tick += (_, _) => { _rescanDebounce.Stop(); SyncWithDisk(); };
        _mixDebounce.Tick += (_, _) => { _mixDebounce.Stop(); _mpv?.SetAudioGraph(AudioGraph()); };
        Timeline.SeekRequested += t => { _mpv?.Seek(t); UpdateTimeText(t); };
        // While dragging: just follow along. On release: save and start looping the new range.
        Timeline.TrimChanged += (i, o) => { _edit.In = i; _edit.Out = o; UpdateTrimText(); };
        Timeline.TrimCommitted += (i, o) => { _edit.In = i; _edit.Out = o; TrimEdited(); TogglePreview(true); };

        SourceInitialized += (_, _) => App.UseDarkTitleBar(this);
        Loaded += (_, _) => LoadLibrary();
        // Scrolling, resizing and filtering all change the scroll extent/offset: recheck which cards are near the viewport.
        ClipGrid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => ScheduleViewUpdate()));
        Closing += (_, _) => Shutdown();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ================================================================== library

    void LoadLibrary()
    {
        EmptyHint.Text = $"Press {_settings.SaveClipHotkey} in a game and the clip shows up here.";
        foreach (var c in _index.Scan()) _items.Add(new ClipItem(c));
        RefreshSidebar();
        WatchFolder();
        var fill = FillMetadataAsync(_items.ToList());
        _ = CheckForUpdateAsync();
        if (_openOnStart != null)
        {
            var target = _items.FirstOrDefault(i => string.Equals(i.Path, Path.GetFullPath(_openOnStart), StringComparison.OrdinalIgnoreCase));
            if (target != null) _ = fill.ContinueWith(_ => Dispatcher.Invoke(() => OpenClip(target)));
        }
    }

    /// <summary>Probes clips and creates thumbnails in the background, newest first.</summary>
    async Task FillMetadataAsync(List<ClipItem> items)
    {
        var tasks = items.Select(async item =>
        {
            try
            {
                if (_index.NeedsProbe(item.Clip)) await _index.ProbeAsync(item.Clip, _cts.Token);
                await _index.EnsureThumbnailAsync(item.Clip, _cts.Token);
                await Dispatcher.InvokeAsync(item.Refresh);
            }
            catch (OperationCanceledException) { }
        }).ToList();
        await Task.WhenAll(tasks);
        _index.SaveCache();
        Dispatcher.Invoke(RefreshSidebar);
    }

    void WatchFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.ClipsFolder);
            _watcher = new FileSystemWatcher(_settings.ClipsFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _rescanDebounce.Stop(); _rescanDebounce.Start(); });
            _watcher.Created += h; _watcher.Deleted += h;
            _watcher.Renamed += (s, e) => h(s, e); // the recorder writes .mp4.part, then renames to .mp4
        }
        catch (Exception ex) { Log.Warn($"Folder watch failed: {ex.Message}"); }
    }

    /// <summary>Adds new clips and drops deleted ones without reloading everything.</summary>
    void SyncWithDisk()
    {
        var onDisk = _index.Scan();
        var known = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var diskPaths = onDisk.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var gone in _items.Where(i => !diskPaths.Contains(i.Path)).ToList())
            _items.Remove(gone);
        var added = onDisk.Where(c => !known.ContainsKey(c.Path)).Select(c => new ClipItem(c)).ToList();
        foreach (var a in added) _items.Add(a);
        RefreshSidebar();
        if (added.Count > 0) _ = FillMetadataAsync(added);
    }

    void RefreshSidebar()
    {
        var selected = _gameFilter;
        var games = _items.GroupBy(i => i.Game).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new GameEntry(g.Key, g.Count())).ToList();
        games.Insert(0, new GameEntry("", _items.Count, IsAll: true));
        games.Insert(1, new GameEntry(FavKey, _items.Count(i => i.IsFavorite), IsFavorites: true));
        GameList.SelectionChanged -= GameList_SelectionChanged;
        GameList.ItemsSource = games;
        GameList.SelectedItem = games.FirstOrDefault(g => g.Name == selected) ?? games[0];
        GameList.SelectionChanged += GameList_SelectionChanged;
        if (GameList.SelectedItem is GameEntry ge && ge.Name != _gameFilter) { _gameFilter = ge.Name; _view.Refresh(); }

        long total = _items.Sum(i => i.SizeBytes);
        StorageText.Text = $"{_items.Count} clips · {ClipItem.FormatSize(total)}\n{_settings.ClipsFolder}";
        UpdateHeader();
    }

    void UpdateHeader()
    {
        HeaderText.Text = _gameFilter.Length == 0 ? "All clips" : _gameFilter == FavKey ? "Favorites" : _gameFilter;
        var visible = _view.Cast<ClipItem>().ToList();
        int shown = visible.Count;
        CountText.Text = (shown == 1 ? "1 clip" : $"{shown} clips") + (shown > 0 ? $"  ·  {ClipItem.FormatSize(visible.Sum(c => c.SizeBytes))}" : "");
        UpdateDayGroups();
        ScheduleViewUpdate();
        EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count > 0 && shown == 0)
        {
            EmptyHint.Text = "No clips match your filter.";
            ((TextBlock)EmptyState.Children[0]).Text = "Nothing here";
        }
    }

    void ApplySort(string key)
    {
        // Newest/oldest: grouped under day headers. Longest/largest: one flat grid (days would scatter).
        bool byDay = key is "newest" or "oldest";
        _view.GroupDescriptions.Clear();
        if (byDay) _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClipItem.Day)));
        if (ClipItem.GroupedByDay != byDay)
        {
            ClipItem.GroupedByDay = byDay;
            foreach (var c in _items) c.RefreshSubtitle();
        }
        _view.SortDescriptions.Clear();
        _view.SortDescriptions.Add(key switch
        {
            "oldest" => new SortDescription(nameof(ClipItem.Created), ListSortDirection.Ascending),
            "longest" => new SortDescription(nameof(ClipItem.Duration), ListSortDirection.Descending),
            "largest" => new SortDescription(nameof(ClipItem.SizeBytes), ListSortDirection.Descending),
            _ => new SortDescription(nameof(ClipItem.Created), ListSortDirection.Descending),
        });
    }

    void Sort_Checked(object sender, RoutedEventArgs e)
    {
        if (_view == null) return; // fires during InitializeComponent
        ApplySort((string)((FrameworkElement)sender).Tag);
        UpdateDayGroups();
    }

    void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _gameFilter = (GameList.SelectedItem as GameEntry)?.Name ?? "";
        _view.Refresh();
        UpdateHeader();
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        _view.Refresh();
        UpdateHeader();
    }

    ClipItem? _pressed;

    // Nothing selected: a plain click opens the clip, and the corner circle (or Ctrl/Shift+click) starts selecting.
    // Anything selected (select mode): a plain click toggles the card instead.
    void ClipGrid_LeftDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = null;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (IsInCardButton(e.OriginalSource as DependencyObject)) return; // CardCheck_Down / Star_Down handle it
        if (ItemFrom(e.OriginalSource) is not ClipItem c) return;
        _pressed = c;
        e.Handled = true; // no ListBox selection
    }

    void ClipGrid_LeftUp(object sender, MouseButtonEventArgs e)
    {
        var c = _pressed;
        _pressed = null;
        if (c == null || ItemFrom(e.OriginalSource) != c) return;
        e.Handled = true;
        if (ClipGrid.SelectedItems.Count == 0) OpenClip(c);
        else if (ClipGrid.SelectedItems.Contains(c)) ClipGrid.SelectedItems.Remove(c);
        else ClipGrid.SelectedItems.Add(c);
    }

    static bool IsInCardButton(DependencyObject? d)
    {
        for (; d != null && d is not ListBoxItem; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { Name: "check" or "star" }) return true;
        return false;
    }

    void ClipGrid_KeyDown(object sender, KeyEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (e.Key == Key.Enter && sel.Count == 1) { OpenClip(sel[0]); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteClips(sel); e.Handled = true; }
        else if (e.Key == Key.Escape) { ClipGrid.UnselectAll(); e.Handled = true; }
        else if (e.Key == Key.F2 && sel.Count == 1) { RenameClip(sel[0]); e.Handled = true; }
    }

    static ClipItem? ItemFrom(object source) =>
        (source as FrameworkElement)?.DataContext as ClipItem
        ?? ((source as FrameworkContentElement)?.DataContext as ClipItem);

    ClipItem? _contextItem;

    void ClipGrid_RightDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click on a selected card keeps the selection (act on all of it); on another card, select just that one.
        _contextItem = ItemFrom(e.OriginalSource);
        if (_contextItem != null && !ClipGrid.SelectedItems.Contains(_contextItem)) ClipGrid.SelectedItem = _contextItem;
    }

    void ClipGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) == null) { e.Handled = true; return; } // right-click on empty space: no menu
        int n = ClipGrid.SelectedItems.Count;
        CtxOpen.Visibility = CtxRename.Visibility = n == 1 ? Visibility.Visible : Visibility.Collapsed;
        bool allFav = ClipGrid.SelectedItems.Cast<ClipItem>().All(c => c.IsFavorite);
        CtxFav.Header = allFav ? "Remove from favorites" : "Add to favorites";
        CtxExport.Header = n == 1 ? "Export for Discord" : $"Export {n} clips for Discord";
        CtxDelete.Header = n == 1 ? "Delete (Recycle Bin)" : $"Delete {n} clips (Recycle Bin)";
    }

    void CtxOpen_Click(object sender, RoutedEventArgs e) { if (Selected() is [var c]) OpenClip(c); }
    void CtxSelectDay_Click(object sender, RoutedEventArgs e) { if (_contextItem != null) SelectDay(_contextItem.Day, toggle: false); }

    // ================================================================== selection

    /// <summary>Selected clips, newest first.</summary>
    List<ClipItem> Selected() => ClipGrid.SelectedItems.Cast<ClipItem>().OrderByDescending(c => c.Created).ToList();

    void ClipGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionUi();

    void UpdateSelectionUi()
    {
        var sel = Selected();
        ClipGrid.Tag = sel.Count > 1 ? "multi" : null;
        bool busy = _batchCts != null;
        SelectionBar.Visibility = sel.Count > 0 || busy || _batchPaths != null ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = sel.Count == 1 ? "1 clip selected" : $"{sel.Count} clips selected";
        SelectionDetail.Text = sel.Count == 0 ? "" :
            $"{ClipItem.FormatSize(sel.Sum(c => c.SizeBytes))}  ·  {TrimTimeline.Fmt(sel.Sum(c => c.Duration))} total";
        SelOpenBtn.Visibility = sel.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        SelExportBtn.IsEnabled = sel.Count > 0 && !busy;
        UpdateDayGroups();
    }

    /// <summary>Header text and select state for every day group currently shown.</summary>
    void UpdateDayGroups()
    {
        if (_view.Groups == null) return;
        var selected = ClipGrid.SelectedItems.Cast<ClipItem>().ToHashSet();
        foreach (CollectionViewGroup g in _view.Groups)
        {
            if (g.Name is not DayGroup day) continue;
            var clips = g.Items.Cast<ClipItem>().ToList();
            day.Summary = $"{(clips.Count == 1 ? "1 clip" : $"{clips.Count} clips")}  ·  {ClipItem.FormatSize(clips.Sum(c => c.SizeBytes))}";
            int n = clips.Count(selected.Contains);
            day.State = n == 0 ? 0 : n == clips.Count ? 2 : 1;
        }
    }

    void DayHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CollectionViewGroup { Name: DayGroup day }) SelectDay(day, toggle: true);
        e.Handled = true;
    }

    /// <summary>Adds the day's clips to the selection; with toggle, a fully selected day is unselected instead.</summary>
    void SelectDay(DayGroup day, bool toggle)
    {
        var clips = _view.Cast<ClipItem>().Where(c => c.Day == day).ToList();
        bool unselect = toggle && clips.All(ClipGrid.SelectedItems.Contains);
        foreach (var c in clips)
        {
            if (unselect) ClipGrid.SelectedItems.Remove(c);
            else if (!ClipGrid.SelectedItems.Contains(c)) ClipGrid.SelectedItems.Add(c);
        }
        ClipGrid.Focus();
    }

    void CardCheck_Down(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClipItem c) return;
        if (ClipGrid.SelectedItems.Contains(c)) ClipGrid.SelectedItems.Remove(c);
        else ClipGrid.SelectedItems.Add(c);
        e.Handled = true; // don't let the card click reset the selection to just this one
    }

    void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ClipGrid.UnselectAll();
        if (_batchCts == null) { _batchPaths = null; BatchPanel.Visibility = Visibility.Collapsed; UpdateSelectionUi(); }
    }

    void SelOpen_Click(object sender, RoutedEventArgs e) { if (Selected() is [var c]) OpenClip(c); }

    void SelShow_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 1) { ShowInFolder(sel[0].Path); return; }
        // Explorer can only pre-select one file per window: open each game folder once.
        foreach (var folder in sel.GroupBy(c => Path.GetDirectoryName(c.Path)).Take(5))
            ShowInFolder(folder.First().Path);
    }

    void SelCopy_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(sel.Select(c => c.Path).ToArray());
        Clipboard.SetFileDropList(files);
        SelectionDetail.Text = $"Copied {sel.Count} file{(sel.Count == 1 ? "" : "s")}: paste with Ctrl+V";
    }

    void SelDelete_Click(object sender, RoutedEventArgs e) => DeleteClips(Selected());

    // ================================================================== favorites, rename

    void Star_Down(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem c) SetFavorite(new() { c }, !c.IsFavorite);
        e.Handled = true; // not a click on the card
    }

    void CtxFav_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        SetFavorite(sel, !sel.All(c => c.IsFavorite));
    }

    void SetFavorite(List<ClipItem> clips, bool favorite)
    {
        foreach (var c in clips)
        {
            var edit = ClipEdits.Get(c.Path);
            edit.Favorite = favorite;
            ClipEdits.Set(c.Path, edit);
            if (_current == c) _edit.Favorite = favorite; // the player saves its copy on every trim
            c.IsFavorite = favorite;
        }
        if (_gameFilter == FavKey) _view.Refresh();
        RefreshSidebar();
    }

    void CtxRename_Click(object sender, RoutedEventArgs e) { if (Selected() is [var c]) RenameClip(c); }

    void RenameClip(ClipItem c)
    {
        var name = RenameDialog.Ask(this, c.Title);
        if (name == null || name == c.Title) return;
        var target = Path.Combine(Path.GetDirectoryName(c.Path)!, name + ".mp4");
        if (File.Exists(target) && !string.Equals(target, c.Path, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"There is already a clip called \"{name}\" in this folder.", "Rename clip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_current == c) CloseClip();
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(c.Path, target); break; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(100); } // the player may still be letting go of it
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't rename the clip: {ex.Message}", "Rename clip", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        ClipEdits.Move(c.Path, target);
        Log.Info($"Renamed {c.Path} -> {target}");
        SyncWithDisk();
    }

    // ================================================================== hover scrub

    void Thumb_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipItem c } fe || fe.ActualWidth <= 0) return;
        if (c.Storyboard == null) { RequestStoryboard(c); return; }
        c.Scrub(e.GetPosition(fe).X / fe.ActualWidth);
    }

    void Thumb_MouseLeave(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem c) c.EndScrub();
    }

    async void RequestStoryboard(ClipItem c)
    {
        if (c.StoryboardRequested) return;
        c.StoryboardRequested = true;
        try
        {
            c.StoryboardPath ??= await _index.EnsureStoryboardAsync(c.Clip, _cts.Token);
            if (c.StoryboardPath != null && c.InView) c.SetStoryboard(c.StoryboardPath);
        }
        catch (OperationCanceledException) { }
        finally { c.StoryboardRequested = c.StoryboardPath == null; } // a failed clip isn't retried on every mouse move
    }

    // ================================================================== what's on screen

    bool _viewUpdatePending;
    ScrollViewer? _gridScroll;

    void ScheduleViewUpdate()
    {
        if (_viewUpdatePending) return;
        _viewUpdatePending = true;
        Dispatcher.BeginInvoke(() => { _viewUpdatePending = false; UpdateInView(); }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Marks cards within one screen of the viewport (only those decode their images) and shows the
    /// sticky header for the day at the top once that day's own header has scrolled out of view.
    /// </summary>
    void UpdateInView()
    {
        _gridScroll ??= FindChild<ScrollViewer>(ClipGrid);
        if (_gridScroll == null || !ClipGrid.IsVisible) return;
        double h = _gridScroll.ViewportHeight;
        var near = new HashSet<ClipItem>();
        CollectionViewGroup? current = null;
        double currentTop = double.NegativeInfinity;
        Walk(_gridScroll);
        foreach (var item in _items) item.InView = near.Contains(item);

        bool sticky = current != null && currentTop < -4;
        StickyHeader.Visibility = sticky ? Visibility.Visible : Visibility.Collapsed;
        if (sticky) StickyContent.Content = current;

        void Walk(DependencyObject d)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
                if (child is ListBoxItem { DataContext: ClipItem ci } lbi)
                {
                    double y = lbi.TranslatePoint(new Point(0, 0), _gridScroll).Y;
                    if (y + lbi.ActualHeight > -h && y < 2 * h) near.Add(ci);
                    continue; // nothing to find inside a card
                }
                if (child is GroupItem { DataContext: CollectionViewGroup g } gi)
                {
                    double y = gi.TranslatePoint(new Point(0, 0), _gridScroll).Y;
                    if (y <= 0 && y > currentTop) { current = g; currentTop = y; }
                }
                Walk(child);
            }
        }
    }

    static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.ClipsFolder);
        Process.Start(new ProcessStartInfo(_settings.ClipsFolder) { UseShellExecute = true });
    }

    static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    void DeleteClips(List<ClipItem> clips)
    {
        if (clips.Count == 0) return;
        string what = clips.Count == 1 ? $"\"{clips[0].Clip.FileName}\"" :
            $"{clips.Count} clips ({ClipItem.FormatSize(clips.Sum(c => c.SizeBytes))})";
        int favs = clips.Count(c => c.IsFavorite);
        string favNote = favs == 0 ? "" : clips.Count == 1 ? "\n\nThis clip is a favorite." : $"\n\n{favs} of them are favorites.";
        var r = MessageBox.Show(this, $"Move {what} to the Recycle Bin?{favNote}\n\nYou can restore {(clips.Count == 1 ? "it" : "them")} from the Recycle Bin.",
            clips.Count == 1 ? "Delete clip" : "Delete clips", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (r != MessageBoxResult.OK) return;

        var open = _current != null && clips.Contains(_current) ? _current : null;
        if (open != null) CloseClip(); // mpv must release the file first
        var failed = RecycleBin.SendMany(clips.Select(c => c.Path).ToList()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in clips.Where(c => !failed.Contains(c.Path)))
        {
            _items.Remove(c);
            ClipEdits.Remove(c.Path);
            Log.Info($"Moved to Recycle Bin: {c.Path}");
        }
        RefreshSidebar();
        if (failed.Count > 0)
        {
            MessageBox.Show(this, $"Couldn't move {failed.Count} clip{(failed.Count == 1 ? "" : "s")} to the Recycle Bin (open in another app?).",
                "Delete clips", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (open != null && failed.Contains(open.Path)) OpenClip(open);
        }
    }

    // ================================================================== player

    void OpenClip(ClipItem c)
    {
        _current = c;
        _edit = ClipEdits.Get(c.Path);
        LibraryView.Visibility = Visibility.Collapsed;
        PlayerView.Visibility = Visibility.Visible;
        PlayerTitle.Text = c.Title;
        PlayerSubtitle.Text = $"{c.Game}  ·  {c.Subtitle}";
        PlayerError.Visibility = Visibility.Collapsed;
        PreviewToggle.IsChecked = false;

        var tracks = c.Clip.SeparateTracks;
        MixPanel.IsEnabled = tracks != null;
        MixPanel.ToolTip = tracks == null ? "This clip has a single audio track" : null;
        _loadingClip = true;
        GameVol.Value = _edit.GameVolumeDb;
        MicVol.Value = _edit.MicVolumeDb;
        _loadingClip = false;
        UpdateVolumeLabels();

        Timeline.SetClip(c.Duration, _edit.In, _edit.EffectiveOut(c.Duration));
        Timeline.Position = 0;
        UpdateTrimText();
        UpdateTimeText(0);

        if (!EnsurePlayer()) return;
        _mpv!.SetLoop(null, null);
        _mpv.Load(c.Path, AudioGraph());
        Focus();
    }

    bool EnsurePlayer()
    {
        if (_mpv != null) return true;
        try
        {
            if (Video.Hwnd == IntPtr.Zero) UpdateLayout();
            _mpv = new MpvPlayer(Video.Hwnd);
            _mpv.StateChanged += OnPlayerState;
            _mpv.FileLoaded += OnFileLoaded;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Player start failed", ex);
            PlayerError.Text = ex is FileNotFoundException ? Ffmpeg.MpvInstallHint : $"The player couldn't start: {ex.Message}";
            PlayerError.Visibility = Visibility.Visible;
            return false;
        }
    }

    void OnFileLoaded()
    {
        if (_current == null || _mpv == null) return;
        // Duration from the player is exact; prefer it over the probe.
        if (_mpv.Duration > 0 && Math.Abs(_mpv.Duration - Timeline.Duration) > 0.05)
            Timeline.SetClip(_mpv.Duration, _edit.In, _edit.EffectiveOut(_mpv.Duration));
        // Start where the trimmed clip starts.
        if (_edit.In > 0) _mpv.Seek(_edit.In);
        UpdateTrimText();
    }

    void OnPlayerState()
    {
        if (_mpv == null) return;
        if (_mpv.Duration > 0 && Timeline.Duration <= 0) Timeline.SetClip(_mpv.Duration, _edit.In, _edit.EffectiveOut(_mpv.Duration));
        Timeline.Position = _mpv.Position;
        UpdateTimeText(_mpv.Position);
        PlayIcon.Text = _mpv.Paused ? "" : "";
    }

    void CloseClip()
    {
        _mpv?.SetLoop(null, null);
        _mpv?.Stop();
        _current = null;
        PlayerView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
        ClipGrid.Focus();
        ScheduleViewUpdate();
    }

    /// <summary>mpv filter graph that mixes the separate game and mic tracks with the chosen volumes.</summary>
    string? AudioGraph()
    {
        if (_current?.Clip.SeparateTracks is not { } tracks) return null;
        var (g, m) = tracks;
        string Db(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        return $"[aid{g + 1}]volume={Db(GameVol.Value)}dB[ga];" +
               $"[aid{m + 1}]volume={Db(MicVol.Value)}dB,aformat=channel_layouts=stereo[ma];" +
               "[ga][ma]amix=inputs=2:normalize=0,alimiter=limit=0.95:level=0[ao];[vid1]null[vo]";
    }

    void TrimEdited()
    {
        if (_current == null) return;
        ClipEdits.Set(_current.Path, _edit);
        UpdateTrimText();
        if (PreviewToggle.IsChecked == true) _mpv?.SetLoop(_edit.In, _edit.EffectiveOut(Timeline.Duration));
    }

    void UpdateTrimText()
    {
        double dur = Timeline.Duration;
        double len = _edit.EffectiveOut(dur) - _edit.In;
        TrimText.Text = _edit.IsTrimmed(dur) ? $"Trimmed: {TrimTimeline.Fmt(len)} of {TrimTimeline.Fmt(dur)}" : "";
    }

    void UpdateTimeText(double t) =>
        TimeText.Text = $"{TrimTimeline.Fmt(t)}.{(int)(Math.Max(0, t) * 10 % 10)}  /  {TrimTimeline.Fmt(Timeline.Duration)}";

    void UpdateVolumeLabels()
    {
        static string L(double v) => v == 0 ? "0 dB" : $"{v:+0;-0} dB";
        GameVolText.Text = L(GameVol.Value);
        MicVolText.Text = L(MicVol.Value);
    }

    void SetIn()
    {
        if (_mpv == null) return;
        _edit.In = Math.Min(_mpv.Position, _edit.EffectiveOut(Timeline.Duration) - 0.5);
        if (_edit.In < 0) _edit.In = 0;
        Timeline.SetClip(Timeline.Duration, _edit.In, _edit.EffectiveOut(Timeline.Duration));
        TrimEdited();
        TogglePreview(true);
    }

    void SetOut()
    {
        if (_mpv == null) return;
        _edit.Out = Math.Max(_mpv.Position, _edit.In + 0.5);
        Timeline.SetClip(Timeline.Duration, _edit.In, _edit.Out);
        TrimEdited();
        TogglePreview(true);
    }

    void TogglePreview(bool? on = null)
    {
        bool enable = on ?? PreviewToggle.IsChecked != true;
        PreviewToggle.IsChecked = enable;
        if (_mpv == null) return;
        if (enable)
        {
            _mpv.SetLoop(_edit.In, _edit.EffectiveOut(Timeline.Duration));
            _mpv.Seek(_edit.In);
            _mpv.SetPause(false);
        }
        else _mpv.SetLoop(null, null);
    }

    void Step(int dir)
    {
        if (_mpv == null) return;
        if (!_mpv.Paused) _mpv.SetPause(true);
        if (dir > 0) _mpv.FrameStep(); else _mpv.FrameBackStep();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PlayerView.Visibility != Visibility.Visible)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); e.Handled = true; }
            return;
        }
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: _mpv?.TogglePause(); break;
            case Key.Left when shift: _mpv?.SeekBy(-5); break;
            case Key.Right when shift: _mpv?.SeekBy(5); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(+1); break;
            case Key.J: _mpv?.SeekBy(-10); break;
            case Key.L: _mpv?.SeekBy(10); break;
            case Key.I: SetIn(); break;
            case Key.O: SetOut(); break;
            case Key.P: TogglePreview(); break;
            case Key.Home: _mpv?.Seek(_edit.In); break;
            case Key.End: _mpv?.Seek(Math.Max(_edit.In, _edit.EffectiveOut(Timeline.Duration) - 0.05)); break;
            case Key.E: _ = ExportCurrent(); break;
            case Key.Escape: CloseClip(); break;
            case Key.Delete: if (_current != null) DeleteClips(new() { _current }); break;
            default: return;
        }
        e.Handled = true;
    }

    void Back_Click(object sender, RoutedEventArgs e) => CloseClip();
    void Play_Click(object sender, RoutedEventArgs e) => _mpv?.TogglePause();
    void StepBack_Click(object sender, RoutedEventArgs e) => Step(-1);
    void StepFwd_Click(object sender, RoutedEventArgs e) => Step(+1);
    void SetIn_Click(object sender, RoutedEventArgs e) => SetIn();
    void SetOut_Click(object sender, RoutedEventArgs e) => SetOut();
    void Preview_Click(object sender, RoutedEventArgs e) => TogglePreview(PreviewToggle.IsChecked == true);
    void PlayerShow_Click(object sender, RoutedEventArgs e) { if (_current != null) ShowInFolder(_current.Path); }
    void PlayerDelete_Click(object sender, RoutedEventArgs e) { if (_current != null) DeleteClips(new() { _current }); }

    void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        _edit.In = 0; _edit.Out = 0;
        Timeline.SetClip(Timeline.Duration, 0, Timeline.Duration);
        TrimEdited();
        TogglePreview(false);
    }

    void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _loadingClip) return;
        if (sender is Slider s) s.Value = Math.Round(s.Value);
        UpdateVolumeLabels();
        if (_current == null) return;
        _edit.GameVolumeDb = GameVol.Value;
        _edit.MicVolumeDb = MicVol.Value;
        ClipEdits.Set(_current.Path, _edit);
        _mixDebounce.Stop(); _mixDebounce.Start();
    }

    void GameVol_DoubleClick(object sender, MouseButtonEventArgs e) => GameVol.Value = 0;
    void MicVol_DoubleClick(object sender, MouseButtonEventArgs e) => MicVol.Value = 0;

    // ================================================================== Discord export

    CancellationTokenSource? _exportCts;
    string? _exportPath;
    Point _dragStart;

    async void Export_Click(object sender, RoutedEventArgs e) => await ExportCurrent();

    async Task ExportCurrent()
    {
        if (_current == null || _exportCts != null) return;
        var clip = _current.Clip;
        var edit = new ClipEdit { In = _edit.In, Out = _edit.Out, GameVolumeDb = _edit.GameVolumeDb, MicVolumeDb = _edit.MicVolumeDb };
        if (clip.Duration <= 0 && Timeline.Duration > 0) clip.Duration = Timeline.Duration;

        _exportCts = new CancellationTokenSource();
        _exportPath = null;
        ExportPanel.Visibility = Visibility.Visible;
        ExportBtn.IsEnabled = false;
        CopyFileBtn.IsEnabled = OpenExportBtn.IsEnabled = false;
        CancelExportBtn.Visibility = Visibility.Visible;
        CloseExportBtn.Visibility = Visibility.Collapsed;
        DragTile.Opacity = 0.4;
        ExportFileText.Text = "Preparing…";
        ExportSizeText.Text = "";
        ExportStatus.Foreground = (System.Windows.Media.Brush)FindResource("Text");
        ExportStatus.Text = "Starting export…";
        double len = edit.EffectiveOut(clip.Duration) - edit.In;
        ExportDetail.Text = $"{TrimTimeline.Fmt(len)} clip · target {_settings.DiscordTargetMB:0.#} MB · original is never changed";
        SetExportBar(0);

        var progress = new Progress<(double Fraction, string Status)>(p =>
        {
            SetExportBar(p.Fraction);
            ExportStatus.Text = p.Status;
        });
        try
        {
            var r = await Task.Run(() => DiscordExporter.ExportAsync(clip, edit, _settings, progress, _exportCts.Token));
            _exportPath = r.Path;
            SetExportBar(1);
            ExportStatus.Text = "Ready for Discord";
            ExportSizeText.Text = $"{r.SizeBytes / 1e6:0.0} MB";
            ExportDetail.Text = $"{r.Plan.Describe()} · {r.Elapsed.TotalSeconds:0}s" +
                                (r.Attempts > 1 ? $" · {r.Attempts} passes to fit" : "") +
                                (r.Plan.StreamCopy ? " · starts at the nearest keyframe" : "");
            ExportFileText.Text = Path.GetFileName(r.Path);
            DragTile.Opacity = 1;
            CopyFileBtn.IsEnabled = OpenExportBtn.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            ExportStatus.Text = "Export cancelled";
            ExportFileText.Text = "";
            SetExportBar(0);
        }
        catch (Exception ex)
        {
            Log.Error("Export failed", ex);
            ExportStatus.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            ExportStatus.Text = ex.Message;
            ExportFileText.Text = "";
        }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            ExportBtn.IsEnabled = true;
            CancelExportBtn.Visibility = Visibility.Collapsed;
            CloseExportBtn.Visibility = Visibility.Visible;
        }
    }

    void SetExportBar(double f) =>
        ExportBar.Width = Math.Clamp(f, 0, 1) * ((FrameworkElement)ExportBar.Parent).ActualWidth;

    void DragTile_Down(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    /// <summary>Starts a real Windows file drag, exactly like dragging the file out of Explorer.</summary>
    void DragTile_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _exportPath == null || !File.Exists(_exportPath)) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var data = new DataObject(DataFormats.FileDrop, new[] { _exportPath });
        DragDrop.DoDragDrop(DragTile, data, DragDropEffects.Copy);
    }

    void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (_exportPath == null || !File.Exists(_exportPath)) return;
        // Same clipboard format as Ctrl+C in Explorer, so Ctrl+V in Discord attaches the file.
        Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { _exportPath });
        ExportStatus.Text = "Copied! Press Ctrl+V in Discord";
    }

    void OpenExport_Click(object sender, RoutedEventArgs e) { if (_exportPath != null) ShowInFolder(_exportPath); }
    void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();
    void CloseExport_Click(object sender, RoutedEventArgs e) => ExportPanel.Visibility = Visibility.Collapsed;

    // ================================================================== batch Discord export

    CancellationTokenSource? _batchCts;
    List<string>? _batchPaths;

    async void SelExport_Click(object sender, RoutedEventArgs e)
    {
        var clips = Selected();
        if (clips.Count == 0 || _batchCts != null) return;
        if (clips.Count == 1)
        {
            // One clip: the player's export panel already has the drag tile and details.
            OpenClip(clips[0]);
            await ExportCurrent();
            return;
        }
        await ExportManyAsync(clips);
    }

    async Task ExportManyAsync(List<ClipItem> clips)
    {
        _batchCts = new CancellationTokenSource();
        _batchPaths = null;
        var done = new List<string>();
        int failedCount = 0;
        BatchPanel.Visibility = Visibility.Visible;
        BatchProgress.Visibility = Visibility.Visible;
        BatchCancelBtn.Visibility = Visibility.Visible;
        BatchDragTile.Visibility = BatchCopyBtn.Visibility = Visibility.Collapsed;
        SetBatchBar(0);
        UpdateSelectionUi();
        try
        {
            for (int i = 0; i < clips.Count; i++)
            {
                var clip = clips[i].Clip;
                int n = i;
                var progress = new Progress<(double Fraction, string Status)>(p =>
                {
                    BatchStatus.Text = $"{n + 1} of {clips.Count}: {p.Status}";
                    SetBatchBar((n + p.Fraction) / clips.Count);
                });
                BatchStatus.Text = $"Exporting {n + 1} of {clips.Count}…";
                try
                {
                    // Each clip keeps the trim and volumes saved for it in the player.
                    var edit = ClipEdits.Get(clip.Path);
                    if (clip.Duration <= 0) await _index.ProbeAsync(clip, _batchCts.Token);
                    var r = await Task.Run(() => DiscordExporter.ExportAsync(clip, edit, _settings, progress, _batchCts.Token));
                    done.Add(r.Path);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failedCount++;
                    Log.Error($"Batch export failed for {clip.Path}", ex);
                }
            }
            SetBatchBar(1);
            BatchStatus.Text = failedCount == 0 ? "All exported" : $"{failedCount} failed (see log)";
        }
        catch (OperationCanceledException) { BatchStatus.Text = "Export cancelled"; }
        finally
        {
            _batchCts.Dispose();
            _batchCts = null;
            BatchCancelBtn.Visibility = Visibility.Collapsed;
        }

        if (done.Count > 0)
        {
            _batchPaths = done;
            long total = done.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            BatchDragText.Text = $"Drag {done.Count} file{(done.Count == 1 ? "" : "s")} into Discord  ·  {total / 1e6:0.0} MB";
            BatchDragTile.Visibility = BatchCopyBtn.Visibility = Visibility.Visible;
            BatchProgress.Visibility = failedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateSelectionUi();
    }

    void SetBatchBar(double f) => BatchBar.Width = Math.Clamp(f, 0, 1) * ((FrameworkElement)BatchBar.Parent).ActualWidth;
    void BatchCancel_Click(object sender, RoutedEventArgs e) => _batchCts?.Cancel();

    Point _batchDragStart;
    void BatchDrag_Down(object sender, MouseButtonEventArgs e) => _batchDragStart = e.GetPosition(this);

    void BatchDrag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _batchPaths == null) return;
        var d = e.GetPosition(this) - _batchDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var files = _batchPaths.Where(File.Exists).ToArray();
        if (files.Length > 0) DragDrop.DoDragDrop(BatchDragTile, new DataObject(DataFormats.FileDrop, files), DragDropEffects.Copy);
    }

    void BatchCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_batchPaths == null) return;
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(_batchPaths.Where(File.Exists).ToArray());
        Clipboard.SetFileDropList(files);
        BatchStatus.Text = "Copied! Press Ctrl+V in Discord";
        BatchProgress.Visibility = Visibility.Visible;
        SetBatchBar(1);
    }

    // ================================================================== updates

    UpdateInfo? _update;

    async Task CheckForUpdateAsync()
    {
        VersionText.Text = $"Version {Updater.Current}";
        if (Updater.JustUpdatedFrom("library") is { } from)
        {
            UpdateTitle.Text = "Updated ✓";
            UpdateText.Text = $"You're now on version {Updater.Current} (was {from}).";
            UpdateText.Margin = new Thickness(0, 2, 0, 0);
            UpdateBtn.Visibility = Visibility.Collapsed;
            UpdatePanel.Visibility = Visibility.Visible;
            _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                if (_update == null) UpdatePanel.Visibility = Visibility.Collapsed;
            }));
        }
        _update = await Updater.CheckAsync(_cts.Token);
        if (_update == null) return;
        UpdateTitle.Text = "Update available";
        UpdateText.Text = $"Version {_update.Version} is ready (you have {Updater.Current}). Takes a few seconds; your clips and settings stay.";
        UpdateText.Margin = new Thickness(0, 2, 0, 8);
        UpdateBtn.Visibility = Visibility.Visible;
        UpdatePanel.Visibility = Visibility.Visible;
        if (((App)Application.Current).UpdateNow) Update_Click(this, new RoutedEventArgs());
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;
        if (_exportCts != null || _batchCts != null)
        {
            UpdateText.Text = "Wait for the export to finish, then update.";
            return;
        }
        UpdateBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(f => UpdateBtnText.Text = $"Downloading… {f:P0}");
            var file = await Updater.DownloadAsync(_update, progress, _cts.Token);
            UpdateBtnText.Text = "Installing…";
            UpdateText.Text = "Replay Recorder will close and reopen by itself in a few seconds.";
            await Task.Delay(1500); // long enough to read
            Updater.Install(file, reopenLibrary: true); // invisible: closes both apps, installs, reopens them
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Update failed", ex);
            UpdateText.Text = $"Update failed: {ex.Message}";
            UpdateBtnText.Text = "Try again";
            UpdateBtn.IsEnabled = true;
        }
    }

    // ================================================================== settings

    SettingsUi.SettingsWindow? _settingsWindow;

    public void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        var oldRoot = _settings.ClipsFolder;
        _settingsWindow = new SettingsUi.SettingsWindow { Owner = this };
        bool saved = _settingsWindow.ShowDialog() == true;
        _settingsWindow = null;
        if (!saved) return;

        _settings = Settings.Load();
        EmptyHint.Text = $"Press {_settings.SaveClipHotkey} in a game and the clip shows up here.";
        if (!string.Equals(oldRoot, _settings.ClipsFolder, StringComparison.OrdinalIgnoreCase))
        {
            // Different clips folder: simplest correct thing is a fresh start of the library.
            ((App)Application.Current).Restart();
            return;
        }
        RefreshSidebar();
    }

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    void OpenExportsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.ExportFolder);
        Process.Start(new ProcessStartInfo(_settings.ExportFolder) { UseShellExecute = true });
    }

    void Shutdown()
    {
        _exportCts?.Cancel();
        _batchCts?.Cancel();
        _cts.Cancel();
        _watcher?.Dispose();
        _mpv?.Dispose();
        _index.SaveCache();
    }
}
