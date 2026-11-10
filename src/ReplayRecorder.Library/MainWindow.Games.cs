using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ReplayRecorder.Core;

namespace ReplayRecorder.Library;

/// <summary>
/// Game filter as pills in the top row: All, the games with the most clips, then Favorites.
/// Games that don't fit (or past the fifth) go under a "More" pill that opens a menu.
/// </summary>
public partial class MainWindow
{
    const int MaxGamePills = 5;
    ToggleButton? _morePill;

    void BuildGamePills(IEnumerable<GameEntry> games, int all, int favorites)
    {
        GamePills.Children.Clear();
        GamePills.Children.Add(GamePill(new GameEntry("", all, IsAll: true)));
        foreach (var g in games) GamePills.Children.Add(GamePill(g));
        GamePills.Children.Add(GamePill(new GameEntry(FavKey, favorites, IsFavorites: true)));
        _morePill = new ToggleButton { Style = (Style)FindResource("GamePill") };
        _morePill.Click += MorePill_Click;
        GamePills.Children.Add(_morePill);
        FitGamePills();
    }

    RadioButton GamePill(GameEntry g)
    {
        var text = new TextBlock();
        if (g.IsFavorites)
        {
            var star = new Run(" ") { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 };
            star.SetResourceReference(TextElement.ForegroundProperty, "Star");
            text.Inlines.Add(star);
        }
        text.Inlines.Add(new Run(g.IsAll ? "All".T() : g.Label));
        // Runs share one baseline, so the count (another font) lines up with the name.
        var count = new Run(" " + g.Count);
        count.SetResourceReference(TextElement.FontFamilyProperty, "NumFont");
        count.SetResourceReference(TextElement.ForegroundProperty, "Faint");
        text.Inlines.Add(count);
        var pill = new RadioButton { Style = (Style)FindResource("GamePillRadio"), GroupName = "game", Content = text, Tag = g, IsChecked = g.Name == _gameFilter };
        pill.Checked += (_, _) => SetGameFilter(g.Name);
        return pill;
    }

    void SetGameFilter(string name)
    {
        _gameFilter = name;
        _view.Refresh();
        UpdateHeader();
        SyncMorePill();
    }

    IEnumerable<RadioButton> GameOnlyPills() =>
        GamePills.Children.OfType<RadioButton>().Where(p => p.Tag is GameEntry { IsAll: false, IsFavorites: false });

    /// <summary>Shows as many game pills as fit (at most five); the rest move under More.</summary>
    void FitGamePills()
    {
        if (_morePill == null) return;
        var fixedPills = GamePills.Children.OfType<RadioButton>().Except(GameOnlyPills()).ToList(); // All and Favorites
        var games = GameOnlyPills().ToList();
        static double W(UIElement e)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return e.DesiredSize.Width + 4; // + the pill's right margin
        }
        SetMoreContent("More".T());
        foreach (var p in games) p.Visibility = Visibility.Visible; // collapsed elements measure as 0
        _morePill.Visibility = Visibility.Visible;
        double room = GameHost.ActualWidth - fixedPills.Sum(W) - W(_morePill);
        int shown = 0;
        for (double used = 0; shown < Math.Min(MaxGamePills, games.Count); shown++)
        {
            used += W(games[shown]);
            // The last game needs no room for More.
            if (used > room + (shown == games.Count - 1 ? W(_morePill) : 0)) break;
        }
        for (int i = 0; i < games.Count; i++) games[i].Visibility = i < shown ? Visibility.Visible : Visibility.Collapsed;
        _morePill.Visibility = shown < games.Count ? Visibility.Visible : Visibility.Collapsed;
        SyncMorePill();
    }

    /// <summary>More is lit, and named after the game, while the filter is a game hidden under it.</summary>
    void SyncMorePill()
    {
        if (_morePill == null) return;
        var hidden = GameOnlyPills().FirstOrDefault(p => p.Visibility != Visibility.Visible && p.IsChecked == true);
        _morePill.IsChecked = hidden != null;
        SetMoreContent(hidden != null ? ((GameEntry)hidden.Tag).Name : "More".T());
    }

    void SetMoreContent(string label)
    {
        var text = new TextBlock();
        text.Inlines.Add(new Run(label));
        text.Inlines.Add(new Run(" ") { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9 });
        _morePill!.Content = text;
    }

    void MorePill_Click(object sender, RoutedEventArgs e)
    {
        SyncMorePill(); // a click toggles it; its look follows the filter instead
        var menu = new ContextMenu { PlacementTarget = _morePill, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
        foreach (var p in GameOnlyPills().Where(p => p.Visibility != Visibility.Visible))
        {
            var g = (GameEntry)p.Tag;
            var item = new MenuItem { Header = g.Name, InputGestureText = g.Count.ToString(), FontWeight = p.IsChecked == true ? FontWeights.SemiBold : FontWeights.Normal };
            item.Click += (_, _) =>
            {
                p.IsChecked = true;
                // As with the old dropdown: arrow keys move through the clips again.
                Dispatcher.BeginInvoke(() => ClipGrid.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            };
            menu.Items.Add(item);
        }
        // After a mouse click hands focus back (App.KeepFocusOnMouseClicks, Input priority): opening first, the focus move would close it.
        Dispatcher.BeginInvoke(() => menu.IsOpen = true, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    void GameHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) FitGamePills();
    }
}
