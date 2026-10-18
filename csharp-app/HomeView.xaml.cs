using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Typryt;

/// <summary>The Home tab: counters, recent corrections, and the correction level.</summary>
public partial class HomeView : UserControl
{
    private StatsStore? _stats;
    private PermissionManager? _permissions;

    private sealed record HistoryRow(string Display, string When);

    public HomeView()
    {
        InitializeComponent();
    }

    public void Initialize(StatsStore stats, PermissionManager permissions)
    {
        _stats = stats;
        _permissions = permissions;
        _stats.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        if (_stats is null || _permissions is null) return;

        FixedCount.Text = _stats.TotalFixed.ToString();
        TodayCount.Text = _stats.FixedToday.ToString();
        UndoneCount.Text = _stats.TotalUndone.ToString();

        RecentList.ItemsSource = _stats.History()
            .Select(h => new HistoryRow(
                $"{h.Original} → {h.Corrected}{(h.Undone ? "  (undone)" : "")}",
                TimeAgo(h.At)))
            .ToList();

        var level = _permissions.Level;
        LevelLow.IsChecked = level == CorrectionLevel.Low;
        LevelMedium.IsChecked = level == CorrectionLevel.Medium;
        LevelHigh.IsChecked = level == CorrectionLevel.High;
        LevelExtreme.IsChecked = level == CorrectionLevel.Extreme;
    }

    private void Level_Click(object sender, RoutedEventArgs e)
    {
        if (_permissions is null || sender is not ToggleButton button) return;
        _permissions.Level = CorrectionLevels.Parse((string)button.Tag);
        _permissions.Save();
        Refresh();
    }

    /// <summary>Short relative time, such as "just now", "5 min ago", "3 h ago".</summary>
    private static string TimeAgo(DateTime at)
    {
        var span = DateTime.Now - at;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return at.ToString("yyyy-MM-dd HH:mm");
    }
}
